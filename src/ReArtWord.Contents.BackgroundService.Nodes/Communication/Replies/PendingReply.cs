using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Communication.Replies;

/// <summary>一つの受信に対する一回の応答と期限を所有します。</summary>
internal sealed class PendingReply : IDisposable
{
    private readonly Func<NodeValue, CancellationToken, Task<NodeResult>> respond;
    private readonly CancellationToken lifetime;
    private readonly CancellationTokenRegistration expiration;
    private readonly TaskCompletionSource<NodeResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int state;

    /// <summary>応答先の寿命と一回のwriterを固定します。</summary>
    internal PendingReply(string ownerPeer, string protocol, Func<NodeValue, CancellationToken, Task<NodeResult>> respond, CancellationToken lifetime)
    {
        OwnerPeer = ownerPeer;
        Protocol = protocol;
        this.respond = respond;
        this.lifetime = lifetime;
        expiration = lifetime.Register(Expire);
    }

    /// <summary>実際に受信したPeerです。</summary>
    internal string OwnerPeer
    {
        get;
    }

    /// <summary>応答する接続線種類です。</summary>
    internal string Protocol
    {
        get;
    }

    /// <summary>応答確定または未実行期限切れを待ちます。</summary>
    internal Task<NodeResult> Completion => completion.Task;

    /// <summary>一つのreplyだけにwriterを渡します。</summary>
    /// <returns>実際のwriterが確認した結果です。</returns>
    internal async Task<NodeResult> RespondAsync(NodeValue value, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return NodeResult.Failure("reply.cancelled", "返信開始前に取り消されました。", EOutcome.Cancelled);
        }
        if (lifetime.IsCancellationRequested || Interlocked.CompareExchange(ref state, 1, 0) != 0)
        {
            return NodeResult.Failure("reply.unavailable", "返信先が失効済み、または別の返信が開始済みです。", EOutcome.NotExecuted);
        }
        using CancellationTokenSource operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancellationToken);
        NodeResult result;
        try
        {
            result = await respond(value.Copy(), operation.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException or OperationCanceledException)
        {
            result = NodeResult.Failure("reply.unconfirmed", "返信writerの完了を確認できません。", EOutcome.Unknown);
        }
        Interlocked.Exchange(ref state, 2);
        completion.TrySetResult(result);
        return result;
    }

    /// <summary>writer未開始の場合だけ期限切れを確定します。</summary>
    internal void Expire()
    {
        if (Interlocked.CompareExchange(ref state, 2, 0) == 0)
        {
            completion.TrySetResult(NodeResult.Failure("reply.expired", "返信が開始される前に受付が終了しました。", EOutcome.NotExecuted));
        }
    }

    /// <summary>期限通知を解除します。</summary>
    public void Dispose()
    {
        Expire();
        expiration.Dispose();
    }
}
