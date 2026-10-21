using System.Collections.Concurrent;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Requests;

/// <summary>高頻度の一時通知を永続要求・再送から分離します。</summary>
internal sealed class TransientChannel
{
    private readonly ConcurrentDictionary<string, Func<string, Guid, NodeValue, CancellationToken, Task>> handlers = new(StringComparer.Ordinal);
    private readonly string peerId;
    private readonly Guid instanceId;
    private readonly Func<JsonElement, CancellationToken, Task> publish;
    private readonly Func<bool> localOnly;

    /// <summary>一度送信する通常UDP経路だけを接続します。</summary>
    public TransientChannel(string peerId, Guid instanceId, Func<JsonElement, CancellationToken, Task> publish, Func<bool> localOnly)
    {
        this.peerId = peerId;
        this.instanceId = instanceId;
        this.publish = publish;
        this.localOnly = localOnly;
    }

    /// <summary>通知処理を明示登録します。</summary>
    public void Register(string kind, Func<string, Guid, NodeValue, CancellationToken, Task> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(handler);
        if (!handlers.TryAdd(kind, handler))
        {
            throw new ArgumentException("一時通知は登録済みです。", nameof(kind));
        }
    }

    /// <summary>ローカル購読へ通知し、同じ内容を一度ブロードキャストします。</summary>
    /// <returns>通知処理の完了。全員到達保証ではありません。</returns>
    public async Task PublishAsync(string kind, NodeValue value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(value);
        TransientPayload payload = new(kind, value.Copy());
        JsonElement body = JsonSerializer.SerializeToElement(payload, ContractJson.Options);
        await DispatchAsync(peerId, instanceId, body, cancellationToken).ConfigureAwait(false);
        if (!localOnly())
        {
            await publish(body, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>ローカル配信済みの所有値を再配信・再解析せず、遠隔Peerへだけ一度配送します。</summary>
    /// <returns>遠隔配送の一回の試行です。</returns>
    internal Task PublishRemoteAsync(string kind, NodeValue value, CancellationToken cancellationToken = default)
    {
        if (localOnly())
        {
            return Task.CompletedTask;
        }
        return publish(JsonSerializer.SerializeToElement(new TransientPayload(kind, value), ContractJson.Options), cancellationToken);
    }

    /// <summary>登録通知だけを受信者へ渡し、保存やACKを行いません。</summary>
    /// <returns>通知処理の完了。</returns>
    public async Task DispatchAsync(string sender, Guid instance, JsonElement body, CancellationToken cancellationToken = default)
    {
        if (localOnly() && instance != instanceId)
        {
            return;
        }
        TransientPayload? payload = body.Deserialize<TransientPayload>(ContractJson.Options);
        if (payload is not null && handlers.TryGetValue(payload.Kind, out Func<string, Guid, NodeValue, CancellationToken, Task>? handler))
        {
            await handler(sender, instance, payload.Value, cancellationToken).ConfigureAwait(false);
        }
    }
}
