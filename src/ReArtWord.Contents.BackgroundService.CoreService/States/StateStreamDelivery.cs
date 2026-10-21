using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.InternalData;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.State;
using ReArtWord.Contents.BackgroundService.ManagementTransport.State;
using ReArtWord.Contents.BackgroundService.Runtime.State;

namespace ReArtWord.Contents.BackgroundService.CoreService.States;

/// <summary>表示対象だけを購読し、遅い通信では同じカードの古い表示を最新値へまとめます。</summary>
internal sealed class StateStreamDelivery
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, StateStreamView> pending = new(StringComparer.Ordinal);
    private readonly Channel<bool> changed = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    /// <summary>初回の購読指定と接続の終了監視を、状態変更の待受と同時に所有します。</summary>
    /// <returns>購読と通信の終了です。</returns>
    internal static async Task RunAsync(NetworkStream stream, IObservableNodeStateStore state, string peerId, CancellationToken cancellationToken = default)
    {
        byte[] buffer = new byte[StateStreamWire.MaximumBytes + 4];
        using CancellationTokenSource admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        admission.CancelAfter(TimeSpan.FromSeconds(15));
        int length = await StateStreamWire.ReadAsync(stream, buffer, admission.Token).ConfigureAwait(false);
        if (length == 0)
        {
            return;
        }
        StateStreamTarget[] targets = JsonSerializer.Deserialize<StateStreamTarget[]>(buffer.AsSpan(4, length), ContractJson.Options) ?? [];
        if (targets.Length is < 1 or > 128 || targets.Any(target => target is null || string.IsNullOrWhiteSpace(target.Id) || 200 < target.Id.Length || target.Target?.IsValid() != true)
            || targets.Select(target => target.Id).Distinct(StringComparer.Ordinal).Count() != targets.Length)
        {
            throw new InvalidDataException("Invalid state observation targets.");
        }
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        StateStreamDelivery delivery = new();
        Task[] watches = targets.Select(target => delivery.WatchAsync(state, peerId, target, lifetime.Token)).ToArray();
        Task sending = delivery.SendAsync(stream, lifetime.Token);
        Task closed = WaitForCloseAsync(stream, buffer, lifetime.Token);
        try
        {
            Task completed = await Task.WhenAny(sending, closed).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(watches.Append(sending).Append(closed)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
        }
    }

    /// <summary>後続の受信がない接続でも、ブラウザーの終了を即座に検出します。</summary>
    /// <returns>フレーム境界での正常終了です。</returns>
    private static async Task WaitForCloseAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (await StateStreamWire.ReadAsync(stream, buffer, cancellationToken).ConfigureAwait(false) != 0)
        {
            throw new InvalidDataException("State observation targets are fixed for one connection.");
        }
    }

    /// <summary>表示側の送信待ちを保存処理へ波及させません。</summary>
    /// <returns>一つの状態購読の終了です。</returns>
    private async Task WatchAsync(IObservableNodeStateStore state, string peerId, StateStreamTarget target, CancellationToken cancellationToken = default)
    {
        string scope = target.Target.Scope switch
        {
            EInternalDataScope.Cluster => NodeStateScope.Cluster,
            EInternalDataScope.Peer => NodeStateScope.Peer(peerId, target.Target.WorkflowId),
            _ => NodeStateScope.Shared(target.Target.WorkflowId),
        };
        try
        {
            await foreach (StateChange change in state.WatchAsync(scope, target.Target.Key, cancellationToken).ConfigureAwait(false))
            {
                (string text, bool truncated) = Preview(change.Value);
                Put(new(target.Id, change.Value is not null, text, change.Time, truncated));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is IOException or ChannelClosedException or NotSupportedException or System.Data.Common.DbException)
        {
            Put(new(target.Id, false, string.Empty, DateTimeOffset.UtcNow, Problem: "State observation unavailable"));
        }
    }

    /// <summary>カード数を超える通知を溜めません。</summary>
    private void Put(StateStreamView value)
    {
        using (gate.EnterScope())
        {
            pending[value.Id] = value;
        }
        changed.Writer.TryWrite(true);
    }

    /// <summary>変更通知が来た場合だけ短くまとめ、現在値を有限サイズで送ります。</summary>
    /// <returns>通信の終了です。</returns>
    private async Task SendAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        while (await changed.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (changed.Reader.TryRead(out _))
            {
            }
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            StateStreamView[] batch;
            using (gate.EnterScope())
            {
                batch = pending.Values.ToArray();
                pending.Clear();
            }
            foreach (StateStreamView[] part in batch.Chunk(16))
            {
                using CancellationTokenSource sending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                sending.CancelAfter(TimeSpan.FromSeconds(10));
                await StateStreamWire.WriteAsync(stream, JsonSerializer.SerializeToUtf8Bytes(part, ContractJson.Options), sending.Token).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Binary本文を送らず、JSONも表示上限だけを文字列化します。</summary>
    /// <returns>表示値と省略の有無です。</returns>
    private static (string Text, bool Truncated) Preview(NodeValue? value)
    {
        if (value is null)
        {
            return (string.Empty, false);
        }
        if (value.Kind == EValueKind.Binary)
        {
            return ($"Binary · {value.Binary.Length:N0} bytes", false);
        }
        ReadOnlySpan<byte> json = JsonMarshal.GetRawUtf8Value(value.Json);
        if (json.Length <= 4096)
        {
            return (value.GetString() ?? Encoding.UTF8.GetString(json), false);
        }
        int length = 4096;
        while (0 < length && (json[length] & 0xc0) == 0x80)
        {
            length--;
        }
        return (Encoding.UTF8.GetString(json[..length]) + "…", true);
    }
}
