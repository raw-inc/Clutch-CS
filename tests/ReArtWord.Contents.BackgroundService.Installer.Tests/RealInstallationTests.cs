using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Health;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;
using ReArtWord.Contents.BackgroundService.Installer.Services;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Installer.Tests;

/// <summary>実Windows processとUDPを使い、使い捨ての二つの配置を検証します。</summary>
public sealed class RealInstallationTests
{
    /// <summary>作成、参加、分散更新、再配置、再同期、全削除を実行します。</summary>
    /// <returns>実動作の検証完了です。</returns>
    [RealInstallationFact]
    public async Task WindowsTwoPeerLifecycle()
    {
        string package = Path.GetFullPath(Environment.GetEnvironmentVariable("CLUTCH_TEST_PACKAGE")!);
        string fixture = Path.Combine(Path.GetTempPath(), "clutch-real-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        string cluster = "clutch-test-" + Guid.NewGuid().ToString("N");
        ClientNetworkInterface network = ObserverNetwork.GetInterfaces().Single(item => item.Addresses.Any(address => string.Equals(address.Address, "127.0.0.1", StringComparison.Ordinal)));
        using UdpClient portReservation = new(new IPEndPoint(IPAddress.Any, 0));
        int port = ((IPEndPoint)portReservation.Client.LocalEndPoint!).Port;
        portReservation.Close();
        ClientNetworkBinding binding = new(network.InterfaceId, "127.0.0.1", "127.255.255.255", port);
        InstallationSettings first = new(Path.Combine(fixture, "first"), package, cluster, Environment.MachineName, "first", 1000, true, true, binding, new("Fixture title", "Fixture subtitle"), true);
        InstallationSettings second = first with { Root = Path.Combine(fixture, "second"), PeerId = "second", Priority = 990, NewCluster = false };

        // CIのディスク・通信速度を含め、自己完結型の配布物二式の転送時間を確保します。
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(30));
        try
        {
            List<OperationProgress> progress = [];
            SetupResult created = await InstallerOperations.LocalAsync(new(Guid.NewGuid(), ESetupAction.Install, first), Path.Combine(package, "updater"), progress.Add, timeout.Token).ConfigureAwait(true);
            Assert.True(created.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(created, ContractJson.Options));
            Assert.True(0 < progress[^1].TotalSteps);
            Assert.Equal(progress[^1].TotalSteps, progress[^1].CompletedSteps);
            SetupResult joined = await InstallerOperations.LocalAsync(new(Guid.NewGuid(), ESetupAction.Install, second), Path.Combine(package, "updater"), cancellationToken: timeout.Token).ConfigureAwait(true);
            Assert.True(joined.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(joined, ContractJson.Options));
            await RealClientProbe.CheckAsync(first.Root, timeout.Token).ConfigureAwait(true);
            ObservedTarget[] targets = (await InstallerDiscovery.ScanAsync(binding, timeout.Token).ConfigureAwait(true)).Where(target => string.Equals(target.ClusterId, cluster, StringComparison.Ordinal)).ToArray();
            Assert.Equal(2, targets.Length);
            Assert.All(targets, target => Assert.Equal(Contracts.Cluster.EPeerStatus.Ready, target.Peer!.Status));
            progress.Clear();
            Contracts.Updates.Cluster.ClusterUpdateStatus update = await InstallerOperations.UpdateAsync(targets, Path.Combine(package, "core"), false, progress.Add, timeout.Token).ConfigureAwait(true);
            Assert.True(update.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(new { update.Outcome, update.Reason, update.Peers }, ContractJson.Options));
            Assert.Equal(progress[^1].TotalSteps, progress[^1].CompletedSteps);
            Assert.Contains(progress, value => 0 < value.CompletedBytes && value.CompletedBytes < value.TotalBytes);
            Assert.Equal(progress.Select(value => value.CompletedSteps).Order(), progress.Select(value => value.CompletedSteps));
            foreach (ObservedTarget target in targets)
            {
                OperationProgress[] transferred = progress.Where(value => string.Equals(value.Target, target.PeerId, StringComparison.Ordinal) && 0 < value.TotalBytes).ToArray();
                Assert.NotEmpty(transferred);
                Assert.Equal(transferred[^1].TotalBytes, transferred[^1].CompletedBytes);
            }
            targets = (await InstallerDiscovery.ScanAsync(binding, timeout.Token).ConfigureAwait(true)).Where(target => string.Equals(target.ClusterId, cluster, StringComparison.Ordinal)).ToArray();
            ObservedTarget selected = targets.Single(target => string.Equals(target.PeerId, second.PeerId, StringComparison.Ordinal));
            Infrastructure.Updates.Health.UpdateObserver detachObserver = new(binding, cluster);
            await using (detachObserver.ConfigureAwait(true))
            {
                Contracts.Cluster.PeerInfo observed = await detachObserver.FindAsync(selected.PeerId, selected.Peer!.InstanceId, timeout.Token).ConfigureAwait(true);
                ManagementReply detached = await detachObserver.SendAsync(selected.PeerId, observed.InstanceId, "cluster.detach", null, false, timeout.Token).ConfigureAwait(true);
                Assert.Equal(EOutcome.Succeeded, detached.Outcome);
            }
            SetupResult automatic = await AutomaticRepair.RunAsync(selected, timeout.Token).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, automatic.Outcome);
            InstallationRecord record = await InstallerOperations.InspectAsync(selected, timeout.Token).ConfigureAwait(true);
            SetupResult repaired = await InstallerOperations.RemoteAsync(selected, new(Guid.NewGuid(), ESetupAction.Reinstall, record.InstallationId), timeout.Token).ConfigureAwait(true);
            Assert.True(repaired.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(repaired, ContractJson.Options));
            targets = (await InstallerDiscovery.ScanAsync(binding, timeout.Token).ConfigureAwait(true)).Where(target => string.Equals(target.ClusterId, cluster, StringComparison.Ordinal)).ToArray();
            selected = targets.Single(target => string.Equals(target.PeerId, second.PeerId, StringComparison.Ordinal));
            SetupResult synchronized = await InstallerOperations.RemoteAsync(selected, new(Guid.NewGuid(), ESetupAction.Reinstall, record.InstallationId, KeepDatabase: false, SurvivorConfirmed: true), timeout.Token).ConfigureAwait(true);
            Assert.True(synchronized.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(synchronized, ContractJson.Options));
            targets = (await InstallerDiscovery.ScanAsync(binding, timeout.Token).ConfigureAwait(true)).Where(target => string.Equals(target.ClusterId, cluster, StringComparison.Ordinal)).ToArray();
            foreach (ObservedTarget target in targets.OrderByDescending(target => target.PeerId, StringComparer.Ordinal))
            {
                InstallationRecord installation = await InstallerOperations.InspectAsync(target, timeout.Token).ConfigureAwait(true);
                SetupResult removed = await InstallerOperations.RemoteAsync(target, new(Guid.NewGuid(), ESetupAction.Uninstall, installation.InstallationId, KeepDatabase: false, KeepConfiguration: false), timeout.Token).ConfigureAwait(true);
                Assert.True(removed.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(removed, ContractJson.Options));
                Assert.False(Directory.Exists(Path.Combine(installation.Settings.Root, "core")));
                Assert.False(Directory.Exists(Path.Combine(installation.Settings.Root, "data")));
                WebClientInstallation client = await WebClientCatalog.ReadAsync(Path.Combine(installation.Settings.Root, "client"), timeout.Token).ConfigureAwait(true);
                Assert.True(File.Exists(Path.Combine(client.Root, "Open-Client.cmd")));
                SetupResult removedClient = await InstallerOperations.RemoveClientAsync(new(Guid.NewGuid(), client, false), Path.Combine(package, "updater"), cancellationToken: timeout.Token).ConfigureAwait(true);
                Assert.True(removedClient.Outcome == EOutcome.Succeeded, removedClient.Detail);
                if (OperatingSystem.IsWindows())
                {
                    using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                    Assert.Null(key?.GetValue(installation.ServiceName));
                }
            }
        }
        finally
        {
            Cleanup(fixture);
        }
    }

    /// <summary>この試験の配置から起動したprocessと登録だけを除去します。</summary>
    private static void Cleanup(string fixture)
    {
        foreach (Process process in Process.GetProcessesByName("Clutch-CS.CoreService"))
        {
            using (process)
            {
                string? path = Infrastructure.Processes.ProcessExecutable.GetPath(process);
                if (path is not null && path.StartsWith(fixture + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    if (!process.HasExited)
                    {
                        process.Kill(false);
                    }
                    Assert.True(process.WaitForExit(30000));
                }
            }
        }
        if (OperatingSystem.IsWindows())
        {
            foreach (string path in Directory.EnumerateFiles(fixture, WebClientCatalog.RecordName, SearchOption.AllDirectories))
            {
                WebClientInstallation client = JsonSerializer.Deserialize<WebClientInstallation>(File.ReadAllText(path), ContractJson.Options)!;
                if (client.PerUser && client.Root.StartsWith(fixture + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    WebClientCatalog.Remove(client);
                }
            }
            foreach (string path in Directory.EnumerateFiles(fixture, InstallationFiles.RecordName, SearchOption.AllDirectories))
            {
                InstallationRecord record = JsonSerializer.Deserialize<InstallationRecord>(File.ReadAllText(path), ContractJson.Options)!;
                using Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                string? command = key.GetValue(record.ServiceName)?.ToString();
                if (command is not null && command.Contains(fixture, StringComparison.OrdinalIgnoreCase))
                {
                    key.DeleteValue(record.ServiceName, false);
                }
                InstallationCatalog.Remove(record);
            }
        }
    }
}
