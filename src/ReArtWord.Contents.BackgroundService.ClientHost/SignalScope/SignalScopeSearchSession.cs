using System.Diagnostics.CodeAnalysis;

namespace ReArtWord.Contents.BackgroundService.ClientHost.SignalScope;

/// <summary>取得側の本文検索を一件だけ所有し、取消し後の応答を配送しません。</summary>
internal sealed class SignalScopeSearchSession(SignalScopeHistory history, SignalScopeDelivery delivery) : IAsyncDisposable
{
    private CancellationTokenSource? lifetime;
    private Task work = Task.CompletedTask;

    /// <summary>古い要求を回収してから最新の検索へ切り替えます。</summary>
    /// <returns>検索開始までの操作です。</returns>
    // 抑制理由: workはこの接続が所有し、StopAsyncとDisposeAsyncで必ず終了をawaitします。
    [SuppressMessage("SourceQuality", "RAW511", Justification = "所有した検索Taskは次の開始・明示クリア・接続終了で回収します。")]
    internal async Task StartAsync(SignalScopeRequest request, CancellationToken cancellationToken = default)
    {
        SignalScopeSearchSnapshot snapshot = history.BeginSearch(request);
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        work = RunAsync(snapshot, lifetime.Token);
    }

    /// <summary>停止やクリアで全履歴への参照を残さず終了させます。</summary>
    /// <returns>保留検索の終了です。</returns>
    internal async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (lifetime is null)
        {
            return;
        }
        using CancellationTokenSource current = lifetime;
        lifetime = null;
        await current.CancelAsync().ConfigureAwait(false);
        try
        {
            await work.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>有限バッチで検索し、取得と受信操作へ実行機会を戻します。</summary>
    /// <returns>検索完了または取消しです。</returns>
    private async Task RunAsync(SignalScopeSearchSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        long position = snapshot.FirstId - 1;
        bool reset = true;
        List<long> ids = new(2048);
        while (position < snapshot.LastId)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SignalScopePacket[] batch = history.Batch(snapshot, ref position);
            foreach (SignalScopePacket packet in batch)
            {
                if (snapshot.Query.Matches(packet, snapshot.HostName, snapshot.Names))
                {
                    ids.Add(packet.Row.PacketId);
                }
                if (ids.Count == 2048)
                {
                    await delivery.ControlAsync(SignalScopePacket.Control(new { type = "search", snapshot.RequestId, ids, reset, complete = false }), cancellationToken).ConfigureAwait(false);
                    ids.Clear();
                    reset = false;
                }
            }
            Array.Clear(batch);
            await Task.Yield();
        }
        await delivery.ControlAsync(SignalScopePacket.Control(new { type = "search", snapshot.RequestId, ids, reset, complete = true }), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>接続寿命の終了時に検索を回収します。</summary>
    /// <returns>検索の終了です。</returns>
    // 抑制理由: IAsyncDisposableの固定署名です。取消し済みでも検索の終了を待ちます。
    [SuppressMessage("SourceQuality", "RAW510", Justification = "IAsyncDisposableの固定署名です。")]
    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);
}
