using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Health;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Local;
using ReArtWord.Contents.BackgroundService.Installer.Services;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Installer.Tests;

/// <summary>試験専用のClientHost、CoreとUpdaterでWebクライアント更新を確認します。</summary>
public sealed class RealWebClientUpdateTests
{
    /// <summary>公開版の配置を、現在のユーザーのUpdaterで更新します。</summary>
    /// <returns>実HTTP、タスク登録と設定保持の検証です。</returns>
    [RealInstallationFact(interactive: true)]
    public async Task LocalUpdaterPreservesClientProfile() => await RunAsync(false).ConfigureAwait(true);

    /// <summary>実CoreへUDPで要求し、TCP転送後に独立Updaterが公開版の配置を更新します。</summary>
    /// <returns>Coreを停止しないWebクライアント更新の検証です。</returns>
    [RealInstallationFact(interactive: true)]
    public async Task RemoteUpdaterPreservesClientProfile() => await RunAsync(true).ConfigureAwait(true);

    /// <summary>本番と異なる導入ID、保存先とポートだけを使用します。</summary>
    /// <returns>更新と後始末の完了です。</returns>
    private static async Task RunAsync(bool remote, CancellationToken cancellationToken = default)
    {
        using SetupFixture fixture = new();
        await fixture.InitializeAsync(cancellationToken).ConfigureAwait(true);
        string package = Path.GetFullPath(Environment.GetEnvironmentVariable("CLUTCH_TEST_PACKAGE")!);
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using UdpClient reservation = new(new IPEndPoint(IPAddress.Loopback, 0));
        int managementPort = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
        reservation.Close();
        InstallationSettings settings = fixture.Settings with { PackageRoot = package, ClusterId = "client-update-" + Guid.NewGuid().ToString("N"),
            Network = fixture.Settings.Network with { Port = managementPort }, WebClient = new(true, port), Appearance = new("Retained title", "Retained subtitle") };
        InstallationRecord owner = new(Guid.NewGuid(), settings, "fixture", "unused-core", "unused-updater", "unused-client", DateTimeOffset.UtcNow);
        WebClientInstallation client = (await new WebClientInstaller(fixture.Platform).InstallAsync(owner, cancellationToken).ConfigureAwait(true))!;
        string program = WebClientCatalog.ProgramRoot(client);
        ReleaseManifest previous = await LocalUpdateExecutor.ReadManifestAsync(program, cancellationToken).ConfigureAwait(true);
        Assert.Equal("Clutch-CS.ClientHost.exe", previous.EntryPoint);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(4));
        try
        {
            await WebClientStartup.RegisterAsync(client, timeout.Token).ConfigureAwait(true);
            WebClientUpdateInfo before = await WebClientUpdateProbe.ReadAsync(client, timeout.Token).ConfigureAwait(true);
            string state = WebClientStartup.StateDirectory(client);
            string retained = Path.Combine(state, "retained-update-test.json");
            await File.WriteAllTextAsync(retained, "Keep this profile.", timeout.Token).ConfigureAwait(true);
            SetupResult result;
            if (remote)
            {
                settings = settings with { WebClient = new(false, port, client.Root) };
                SetupResult installed = await fixture.RunAsync(new(Guid.NewGuid(), ESetupAction.Install, settings), "core-install", timeout.Token).ConfigureAwait(true);
                Assert.True(installed.Outcome == EOutcome.Succeeded, installed.Detail);
                InstallationRecord record = await InstallationFiles.ReadAsync(settings.Root, timeout.Token).ConfigureAwait(true);

                // 管理者環境では実サービスから、通常権限では対話ユーザーから更新を要求します。
                TestCoreProcess core = new(Path.Combine(settings.Root, "core", record.CoreEntryPoint), Path.Combine(settings.Root, "config", "contents.toml"), record.ServiceName);
                await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable coreLifetime = core.ConfigureAwait(true);
                await core.StartAsync(timeout.Token).ConfigureAwait(true);
                UpdateObserver observer = new(settings.Network, settings.ClusterId);
                await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = observer.ConfigureAwait(true);
                PeerInfo peer = await WaitForCoreAsync(observer, settings.PeerId, timeout.Token).ConfigureAwait(true);
                await VerifyPreparationCompatibilityAsync(observer, peer, client, previous, timeout.Token).ConfigureAwait(true);
                Assert.Equal(before.Health.Process, (await WebClientUpdateProbe.ReadAsync(client, timeout.Token).ConfigureAwait(true)).Health.Process);
                ObservedTarget target = new(settings.ClusterId, settings.PeerId, settings.Network, peer, record);
                result = await InstallerClientUpdates.RemoteAsync(new(client, target), Path.Combine(package, "client"), cancellationToken: timeout.Token).ConfigureAwait(true);
                Assert.True(core.Running);
                Assert.Equal(peer.InstanceId, (await observer.FindAsync(peer.PeerId, cancellationToken: timeout.Token).ConfigureAwait(true)).InstanceId);
            }
            else
            {
                result = await InstallerClientUpdates.LocalAsync(client, Path.Combine(package, "client"), settings.Network, cancellationToken: timeout.Token).ConfigureAwait(true);
            }
            Assert.True(result.Outcome == EOutcome.Succeeded, result.Stage + " / " + result.Detail);
            client = Assert.IsType<WebClientInstallation>(result.WebClient);
            Assert.Equal(previous.EntryPoint, client.EntryPoint);
            WebClientUpdateInfo after = await WebClientUpdateProbe.ReadAsync(client, timeout.Token).ConfigureAwait(true);
            Assert.NotEqual(before.Health.Process, after.Health.Process);
            Assert.Equal(before.Installation.InstallationId, after.Installation.InstallationId);
            Assert.Equal(port, after.Installation.Port);
            Assert.Equal(state, WebClientStartup.StateDirectory(client));
            Assert.Equal("Keep this profile.", await File.ReadAllTextAsync(retained, timeout.Token).ConfigureAwait(true));
            await WebClientStartup.RegisterAsync(client, timeout.Token).ConfigureAwait(true);
            Assert.Equal(after.Health.Process, (await WebClientUpdateProbe.ReadAsync(client, timeout.Token).ConfigureAwait(true)).Health.Process);
        }
        finally
        {
            client = await WebClientCatalog.ReadAsync(client.Root, false, CancellationToken.None).ConfigureAwait(true);
            await WebClientStartup.StopAsync(client, CancellationToken.None).ConfigureAwait(true);
            await WebClientStartup.UnregisterAsync(client, CancellationToken.None).ConfigureAwait(true);
            WebClientCatalog.Remove(client);
            string state = WebClientStartup.StateDirectory(client);
            InstallationFiles.DeleteDirectory(Path.GetDirectoryName(state)!, client.InstallationId.ToString("N"));
        }
    }

    /// <summary>プロセス起動と参加完了を分け、探索一回の期限切れだけを再確認します。</summary>
    /// <returns>通常参加を確認した試験用Coreです。</returns>
    private static async Task<PeerInfo> WaitForCoreAsync(UpdateObserver observer, string peerId, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(InstallationDefaults.ReadyTimeout);
        while (!timeout.IsCancellationRequested)
        {
            try
            {
                PeerInfo peer = await observer.FindAsync(peerId, cancellationToken: timeout.Token).ConfigureAwait(false);
                if (peer.Status == EPeerStatus.Ready)
                {
                    return peer;
                }
            }
            catch (OperationCanceledException) when (!timeout.IsCancellationRequested)
            {
                // 探索の10秒を、Core全体の起動期限として扱いません。
            }
            await Task.Delay(InstallationDefaults.PollInterval, timeout.Token).ConfigureAwait(false);
        }
        throw new OperationCanceledException("The test Core did not become ready within the startup deadline.", timeout.Token);
    }

    /// <summary>新経路は操作IDだけを返し、旧Installer向けのジョブ応答も維持します。</summary>
    /// <returns>実Coreと配布物の契約確認です。</returns>
    private static async Task VerifyPreparationCompatibilityAsync(UpdateObserver observer, PeerInfo peer, WebClientInstallation client, ReleaseManifest manifest, CancellationToken cancellationToken = default)
    {
        WebClientUpdateRequest request = new(Guid.NewGuid(), client.InstallationId, manifest);
        ManagementReply compact = await observer.SendAsync(peer.PeerId, peer.InstanceId, "setup.client.stage", UpdateObserver.Payload(request), false, cancellationToken).ConfigureAwait(false);
        Assert.True(compact.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(new { compact.Outcome, compact.Reason }, ContractJson.Options));
        Assert.Equal(request.OperationId, compact.Value!.Json.GetGuid());
        ManagementReply legacy = await observer.SendAsync(peer.PeerId, peer.InstanceId, "setup.client.prepare", UpdateObserver.Payload(request), false, cancellationToken).ConfigureAwait(false);
        Assert.True(legacy.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(new { legacy.Outcome, legacy.Reason }, ContractJson.Options));
        WebClientUpdateJob job = legacy.Value!.Json.Deserialize<WebClientUpdateJob>(ContractJson.Options)!;
        Assert.Equal(request.OperationId, job.OperationId);
        Assert.Equal(client, job.Installation);
        Assert.Equal(ContractJson.Hash(manifest), ContractJson.Hash(job.Manifest));
    }
}
