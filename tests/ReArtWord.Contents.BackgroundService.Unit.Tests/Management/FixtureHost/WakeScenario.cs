using System.Net;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;
using ReArtWord.Contents.BackgroundService.CoreService.Management;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Requests;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;

namespace ReArtWord.Contents.BackgroundService.DesktopFixture;

/// <summary>実クラスタ管理輸送を使い、Magic Packetだけは試験所有のループバック受信へ限定します。</summary>
internal static class WakeScenario
{
    /// <summary>送信後の応答を保留し、呼出側の取消しと結果照会を別プロセスで検証します。</summary>
    /// <returns>試験用停止ファイルまたは有限期限での終了です。</returns>
    internal static async Task RunAsync(string configuration, CancellationToken cancellationToken = default)
    {
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(configuration, cancellationToken).ConfigureAwait(false));
        JsonElement settings = document.RootElement;
        string root = settings.GetProperty("directory").GetString()!, clusterId = settings.GetProperty("cluster").GetString()!;
        int port = settings.GetProperty("port").GetInt32(), destinationPort = settings.GetProperty("destinationPort").GetInt32();
        string release = Path.Combine(root, "release"), stop = Path.Combine(root, "stop");
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(TimeSpan.FromSeconds(60));
        PeerCluster peer = new(new(clusterId, "relay", "relay-host", "127.0.0.1", "127.255.255.255", port, 1, true, root), new SqliteRepository(Path.Combine(root, "state.db")))
        {
            ReadinessCheck = _ => ValueTask.FromResult(true),
        };
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable peerLifetime = peer.ConfigureAwait(false);
        WakeOnLanSender sender = new(() => [(IPAddress.Loopback, IPAddress.Loopback, 0)], destinationPort);
        ManagementRegistry registry = new();
        registry.Write("host.wake", async (value, cancellation) =>
        {
            HostWakeResult result = await sender.SendAsync(ManagementRegistry.Body<HostWakeRequest>(value), cancellation).ConfigureAwait(false);
            await File.AppendAllTextAsync(Path.Combine(root, "sent"), "packet\n", cancellation).ConfigureAwait(false);
            while (!File.Exists(release))
            {
                await Task.Delay(20, cancellation).ConfigureAwait(false);
            }
            return result;
        });
        _ = new PeerManagementEndpoint(peer, registry.ExecuteAsync, registry.IsReadOnly, registry.IsTransient);
        await peer.StartAsync(lifetime.Token).ConfigureAwait(false);
        while (!File.Exists(stop))
        {
            await Task.Delay(50, lifetime.Token).ConfigureAwait(false);
        }
    }
}
