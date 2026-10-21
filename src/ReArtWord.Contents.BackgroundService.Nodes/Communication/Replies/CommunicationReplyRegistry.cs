using System.Collections.Concurrent;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Communication.Replies;

/// <summary>受信Peerに属する一回限りの応答先を共有します。</summary>
public sealed class CommunicationReplyRegistry
{
    private readonly ConcurrentDictionary<Guid, PendingReply> requests = [];
    private readonly int maximumPending;
    private int reserved;

    /// <summary>未応答ハンドル数の上限を固定します。</summary>
    public CommunicationReplyRegistry(int maximumPending = 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumPending, 1);
        this.maximumPending = maximumPending;
    }

    /// <summary>現在sourceが所有しているハンドル数です。</summary>
    public int PendingCount => requests.Count;

    /// <summary>source受付時に応答先を一つ登録します。</summary>
    /// <returns>sourceが破棄するハンドル寿命です。</returns>
    internal ReplyRequestLease Register(string ownerPeer, string protocol, Func<NodeValue, CancellationToken, Task<NodeResult>> respond, CancellationToken cancellationToken)
    {
        if (maximumPending < Interlocked.Increment(ref reserved))
        {
            Interlocked.Decrement(ref reserved);
            throw new ReplyCapacityException();
        }
        Guid id = Guid.NewGuid();
        PendingReply request = new(ownerPeer, protocol, respond, cancellationToken);
        if (!requests.TryAdd(id, request))
        {
            using (request)
            {
                Interlocked.Decrement(ref reserved);
                throw new IOException("返信handleの識別子が競合しました。");
            }
        }
        return new(this, id, request);
    }

    /// <summary>ownerとprotocolを照合し、暗黙転送せず一回だけ返信します。</summary>
    /// <returns>writerの結果または未実行です。</returns>
    internal Task<NodeResult> ReplyAsync(string ownerPeer, string protocol, NodeValue handle, NodeValue value, CancellationToken cancellationToken = default)
    {
        string ownerName = "ownerPeer";
        string idName = "id";
        JsonElement json = handle.Json;
        if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty(ownerName, out JsonElement owner) || owner.ValueKind != JsonValueKind.String
            || !string.Equals(owner.GetString(), ownerPeer, StringComparison.Ordinal) || !json.TryGetProperty(idName, out JsonElement identifier)
            || identifier.ValueKind != JsonValueKind.String || !identifier.TryGetGuid(out Guid id)
            || !requests.TryGetValue(id, out PendingReply? pending) || !string.Equals(pending.OwnerPeer, ownerPeer, StringComparison.Ordinal)
            || !string.Equals(pending.Protocol, protocol, StringComparison.Ordinal))
        {
            return Task.FromResult(NodeResult.Failure("reply.handle.unavailable", "このPeerで有効な返信先を確認できません。", EOutcome.NotExecuted));
        }
        return pending.RespondAsync(value, cancellationToken);
    }

    /// <summary>sourceが終了したハンドルと期限通知を解放します。</summary>
    internal void Release(Guid id)
    {
        if (requests.TryRemove(id, out PendingReply? request))
        {
            using (request)
            {
                Interlocked.Decrement(ref reserved);
            }
        }
    }
}
