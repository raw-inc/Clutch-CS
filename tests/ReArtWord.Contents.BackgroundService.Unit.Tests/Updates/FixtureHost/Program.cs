using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Requests;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Health;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.UpdateFixture;

/// <summary>更新試験用の実UDP Peerと実プロセスです。</summary>
internal static class Program
{
    /// <summary>既存管理transportへ稼働確認と固定操作記録後の終了を登録します。</summary>
    /// <returns>正常終了コードです。</returns>
    // 抑制理由: C#固定entrypoint引数とBCL実起動時刻を使用します。
    [SuppressMessage("SourceQuality", "RAW510", Justification = "C# entrypointです。"), SuppressMessage("SourceQuality", "RAW601", Justification = "C# entrypointです。"), SuppressMessage("SourceQuality", "RAW171", Justification = "PID再利用防止に実起動時刻を使います。")]
    private static async Task<int> Main(string[] args)
    {
        int port = int.Parse(args[0], CultureInfo.InvariantCulture);
        string peerId = args[1];
        string root = Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!;
        string nic = NetworkInterface.GetAllNetworkInterfaces().Single(item => item.GetIPProperties().UnicastAddresses.Any(address => address.Address.Equals(System.Net.IPAddress.Loopback))).Id;
        using JsonDocument behavior = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "behavior.json")).ConfigureAwait(false));
        bool ready = behavior.RootElement.GetProperty("ready").GetBoolean();
        string? version = behavior.RootElement.GetProperty("version").GetString();
        SqliteRepository repository = new(Path.Combine(root, "fixture-state", "state.db"));
        PeerCluster cluster = new(new ClusterOptions("fixture-cluster", peerId, peerId, "127.0.0.1", "127.255.255.255", port, 1, true, root) { InterfaceId = nic, HeartbeatInterval = TimeSpan.FromMilliseconds(100) }, repository) { ReadinessCheck = _ => ValueTask.FromResult(true) };
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = cluster.ConfigureAwait(false);
        using Process process = Process.GetCurrentProcess();
        DateTimeOffset started = new(process.StartTime.ToUniversalTime());
        ManagementCommand? stop = null;
        string healthName = "health";
        string stopName = "updates.local.stop";
        PeerManagementEndpoint endpoint = new(cluster, (command, token) =>
        {
            if (string.Equals(command.Operation, healthName, StringComparison.Ordinal))
            {
                return Task.FromResult(new ManagementReply(EOutcome.Succeeded, UpdateObserver.Payload(new { ready, peerId, instanceId = cluster.LocalInstanceId, processId = process.Id, processStartedAt = started, buildVersion = version })));
            }
            if (string.Equals(command.Operation, stopName, StringComparison.Ordinal))
            {
                stop = command;
                return Task.FromResult(new ManagementReply(EOutcome.Succeeded, UpdateObserver.Payload(true)));
            }
            return Task.FromResult(new ManagementReply(EOutcome.NotExecuted));
        }, operation => string.Equals(operation, healthName, StringComparison.Ordinal));
        await cluster.StartAsync().ConfigureAwait(false);
        bool complete = false;
        while (!complete)
        {
            if (stop is not null)
            {
                ClusterRequestStatus status = await cluster.QueryRequestAsync(ManagementWire.Request(stop, false)).ConfigureAwait(false);
                complete = status.Response?.Outcome == EOutcome.Succeeded;
            }
            await Task.Delay(20).ConfigureAwait(false);
        }
        GC.KeepAlive(endpoint);
        return 0;
    }
}
