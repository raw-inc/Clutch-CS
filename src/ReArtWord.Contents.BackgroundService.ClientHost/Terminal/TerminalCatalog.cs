using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.ClientHost.Networking;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Terminal;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Terminal;

/// <summary>全Peerの一覧を一回に集約し、複数ブラウザーの同時照会を共有します。</summary>
internal sealed class TerminalCatalog(ObserverConnection connection, PeerManagementClient remote) : IAsyncDisposable
{
    private readonly Lock sync = new();
    private readonly CancellationTokenSource lifetime = new();
    private Task<TerminalPeerSessions[]>? refresh;
    private readonly List<TerminalPeerSessions> previous = [];
    private DateTimeOffset completed;
    private bool disposed;

    /// <summary>最短3秒間隔・最大4並列で取得し、閲覧者の切断で他の照会を取り消しません。</summary>
    /// <returns>成功または未確認を明示した一覧です。</returns>
    // 抑制理由: 共有refreshはフィールドで所有し、DisposeAsyncで回収します。
    [SuppressMessage("SourceQuality", "RAW511", Justification = "共有取得はrefreshで所有し、DisposeAsyncで完了を待ちます。")]
    internal Task<TerminalPeerSessions[]> ReadAsync(CancellationToken cancellationToken = default)
    {
        using (sync.EnterScope())
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (refresh is null || refresh.IsCompleted && TimeSpan.FromSeconds(3) <= DateTimeOffset.UtcNow - completed)
            {
                refresh = RefreshAsync(lifetime.Token);
            }

            return refresh.WaitAsync(cancellationToken);
        }
    }

    /// <summary>失敗したPeerだけ前回値を残し、別起動実体の状態は混ぜません。</summary>
    /// <returns>一巡の取得結果です。</returns>
    private async Task<TerminalPeerSessions[]> RefreshAsync(CancellationToken cancellationToken = default)
    {
        TerminalPeerSessions[] values = connection.GetClusters().SelectMany(cluster => cluster.Peers.Select(peer =>
            new TerminalPeerSessions(cluster.ClusterId, peer, previous.FirstOrDefault(item => string.Equals(item.ClusterId, cluster.ClusterId, StringComparison.Ordinal) && item.Peer.InstanceId == peer.InstanceId)?.Sessions ?? [], false))).Take(256).ToArray();
        ParallelOptions options = new() { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken };
        await Parallel.ForEachAsync(Enumerable.Range(0, values.Length), options, async (index, token) =>
        {
            TerminalPeerSessions value = values[index];
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                FileTransferPeer target = new(value.ClusterId, value.Peer.PeerId, value.Peer.InstanceId);
                TerminalView[] sessions = await remote.SendAsync<TerminalView[]>(target, "terminal.list", new object(), true, timeout.Token).ConfigureAwait(false);
                values[index] = value with { Sessions = sessions, Confirmed = true };
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or SocketException or JsonException)
            {
                // 一つのPeerの遅延で他の一覧とブラウザーを停止させません。
            }
        }).ConfigureAwait(false);
        using (sync.EnterScope())
        {
            previous.Clear();
            previous.AddRange(values);
            completed = DateTimeOffset.UtcNow;
        }
        return values;
    }

    /// <summary>Host停止時に共有照会を取り消し、完了を確認します。</summary>
    /// <returns>取得タスクの終了です。</returns>
    // 抑制理由: 固定署名の終了処理で、共有Taskをlock外に取り出してawaitします。
    [SuppressMessage("SourceQuality", "RAW510", Justification = "IAsyncDisposable固定署名です。"), SuppressMessage("SourceQuality", "RAW511", Justification = "共有Taskをlock外でawaitします。")]
    public async ValueTask DisposeAsync()
    {
        Task? pending;
        using (sync.EnterScope())
        {
            disposed = true;
            pending = refresh;
        }

        await lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            if (pending is not null)
            {
                await pending.ConfigureAwait(false);
            }
        }

        catch (OperationCanceledException)
        {
        }

        lifetime.Dispose();
    }
}
