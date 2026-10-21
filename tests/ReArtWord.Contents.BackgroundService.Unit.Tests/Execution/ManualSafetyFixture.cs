using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.Safety;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Execution;
using ReArtWord.Contents.BackgroundService.Infrastructure.Execution.Safety;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Execution;

/// <summary>実UDPと独立保存を持つPeerの停止・再起動を検証します。</summary>
internal sealed class ManualSafetyFixture : IAsyncDisposable
{
    private readonly List<(PeerCluster Cluster, ManualSafetyService Safety)> owned = [];
    private readonly string root = Path.Combine(Path.GetTempPath(), "contents-safety-" + Guid.NewGuid().ToString("N"));
    private readonly string clusterId = Guid.NewGuid().ToString("N");
    private readonly int port = Port();

    /// <summary>通常判断を実際の参加状態から評価するノードです。</summary>
    public static NodeDefinition Node => new("node", "test", JsonSerializer.SerializeToElement(new { }), Safety: new(StopWhenSingleton: true, StopWhenDegraded: true));

    /// <summary>現在使用するPeerです。</summary>
    public Dictionary<string, PeerCluster> Peers
    {
        get;
    } = new(StringComparer.Ordinal);

    /// <summary>現在使用する人為安全操作です。</summary>
    public Dictionary<string, ManualSafetyService> Safety
    {
        get;
    } = new(StringComparer.Ordinal);

    /// <summary>再起動で保持する独立した保存領域です。</summary>
    public Dictionary<string, SqliteRepository> Repositories
    {
        get;
    } = new(StringComparer.Ordinal);

    /// <summary>明示新規起点または同じ保存領域の新実体を開始します。</summary>
    /// <returns>起動処理の完了です。</returns>
    public async Task StartAsync(string id, bool fresh = false, bool policyKnown = true, int? networkPort = null, CancellationToken cancellationToken = default)
    {
        if (!Repositories.TryGetValue(id, out SqliteRepository? repository))
        {
            repository = new SqliteRepository(Path.Combine(root, id + ".db"));
            Repositories[id] = repository;
        }
        ClusterOptions options = new(clusterId, id, id, "127.0.0.1", "127.255.255.255", networkPort ?? port, 1, fresh, root)
        {
            HeartbeatInterval = TimeSpan.FromMilliseconds(80),
            PeerTimeout = TimeSpan.FromMilliseconds(450),
            RequestTimeout = TimeSpan.FromSeconds(3),
        };
        PeerCluster peer = new(options, repository) { ReadinessCheck = token => ValueTask.FromResult(true), LocalDetachedExecutionAllowed = command => string.Equals(command.Kind, "safety.change", StringComparison.Ordinal) };
        ManualSafetyService service = new(peer, repository);
        if (policyKnown)
        {
            NodeSafetyGate gate = new(peer);
            service.AutomaticStopCheck = (workflow, scope) => gate.IsAutomaticallyStopped(Node);
        }
        owned.Add((peer, service));
        Peers[id] = peer;
        Safety[id] = service;
        await peer.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>指定した元の実体だけを終了します。</summary>
    /// <returns>当該Peerの停止完了です。</returns>
    public Task StopAsync(string id, CancellationToken cancellationToken = default) => Peers[id].StopAsync(cancellationToken);

    /// <summary>変更要求を一度だけ送り、応答が不明な場合は元操作の結果を照会します。</summary>
    /// <returns>新しい変更を再送せずに成功を確認した受付結果です。</returns>
    internal async Task<DurableReceipt> ChangeAndConfirmAsync(string peer, ManualSafetyChange request, CancellationToken cancellationToken = default)
    {
        DurableReceipt result = await Safety[peer].ChangeAsync(request, cancellationToken).ConfigureAwait(false);
        if (result.Outcome == EOutcome.Unknown)
        {
            result = await ConfirmAsync(peer, result, cancellationToken).ConfigureAwait(false);
        }
        Assert.True(result.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(result));
        return result;
    }

    /// <summary>元要求だけを照会し、確定後に現在の状態を取り込みます。</summary>
    /// <returns>元の操作IDに対応する確定結果です。</returns>
    internal async Task<DurableReceipt> ConfirmAsync(string peer, DurableReceipt result, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (result.Outcome == EOutcome.Unknown)
        {
            try
            {
                await Task.Delay(25, timeout.Token).ConfigureAwait(false);
                DurableReceipt? observed = await Safety[peer].GetOperationAsync(result.OperationId, timeout.Token).ConfigureAwait(false);
                Assert.NotNull(observed);
                result = observed;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
        Assert.True(result.Outcome != EOutcome.Unknown, JsonSerializer.Serialize(result));
        await Safety[peer].RefreshAsync(timeout.Token).ConfigureAwait(false);
        return result;
    }

    /// <summary>件数だけで他Peerの同期中状態を目的の停止と取り違えません。</summary>
    /// <returns>同じ観測一覧で各Peerの期待状態が成立するまでの待機です。</returns>
    internal Task WaitForStatesAsync(string observer, IReadOnlyList<(string PeerId, EPeerStatus Status)> expected, CancellationToken cancellationToken = default)
    {
        return WaitAsync(() =>
        {
            IReadOnlyList<PeerInfo> peers = Peers[observer].GetPeers();
            return expected.All(item => peers.Any(peer => string.Equals(peer.PeerId, item.PeerId, StringComparison.Ordinal) && peer.Status == item.Status));
        }, cancellationToken);
    }

    /// <summary>指定側から見えるReady集合を待ちます。</summary>
    /// <returns>実観測の完了です。</returns>
    public static async Task WaitAsync(Func<bool> condition, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (!condition())
        {
            await Task.Delay(25, timeout.Token).ConfigureAwait(false);
        }
    }

    /// <summary>所有実体を終了してから受入・取込の同期資源を解放します。</summary>
    /// <returns>全所有Peerの解放完了です。</returns>
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        foreach ((PeerCluster cluster, ManualSafetyService safety) in owned.AsEnumerable().Reverse())
        {
            await ((IAsyncDisposable)cluster).DisposeAsync().ConfigureAwait(false);
            safety.Dispose();
        }
    }

    /// <summary>この試験用の空UDPportを取得します。</summary>
    /// <returns>利用するポートです。</returns>
    private static int Port()
    {
        using UdpClient socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }
}
