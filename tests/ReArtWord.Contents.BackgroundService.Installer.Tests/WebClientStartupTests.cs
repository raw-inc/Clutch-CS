using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Installer.Tests;

/// <summary>起動登録が導入・再利用・修復・削除の各操作に含まれることを確認します。</summary>
public sealed class WebClientStartupTests
{
    /// <summary>Coreの修復では登録を補い、Coreだけの削除ではclientを維持します。</summary>
    /// <returns>独立した起動登録の検証です。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task StartupFollowsInstallationScopeAndSurvivesCoreRemoval(bool perUser, bool runAsService)
    {
        using SetupFixture fixture = new();
        await fixture.InitializeAsync();
        InstallationSettings settings = fixture.Settings with { PerUser = perUser, WebClient = new(true, AvailablePort(), RunAsService: runAsService) };
        SetupResult installed = await fixture.RunAsync(new(Guid.NewGuid(), ESetupAction.Install, settings), "install");
        Assert.True(installed.Outcome == EOutcome.Succeeded, installed.Detail);
        WebClientInstallation client = Assert.IsType<WebClientInstallation>(installed.WebClient);
        Assert.True(client.AutomaticStartup);
        Assert.Equal(!runAsService, client.PerUser);
        Assert.Equal(runAsService, client.RunAsService);
        Assert.Equal(["register"], fixture.Platform.ClientCalls);
        InstallationRecord record = await InstallationFiles.ReadAsync(settings.Root);
        fixture.Platform.ClientCalls.Clear();
        SetupResult repaired = await fixture.RunAsync(new(Guid.NewGuid(), ESetupAction.Reinstall, record.Settings, record.InstallationId), "repair");
        Assert.True(repaired.Outcome == EOutcome.Succeeded, repaired.Detail);
        if (runAsService)
        {
            Assert.Equal(["register"], fixture.Platform.ClientCalls);
        }
        else
        {
            Assert.Empty(fixture.Platform.ClientCalls);
        }
        fixture.Platform.ClientCalls.Clear();
        SetupResult removed = await fixture.RunAsync(new(Guid.NewGuid(), ESetupAction.Uninstall, record.Settings, record.InstallationId), "remove-core");
        Assert.True(removed.Outcome == EOutcome.Succeeded, removed.Detail);
        Assert.Empty(fixture.Platform.ClientCalls);
        Assert.Equal(client, fixture.Platform.WebClient);
        SetupResult removedClient = await new WebClientRemoval(fixture.Platform).ExecuteAsync(new(Guid.NewGuid(), client, true), Path.Combine(fixture.Root, "remove-client"));
        Assert.True(removedClient.Outcome == EOutcome.Succeeded, removedClient.Detail);
        Assert.Equal(["unregister"], fixture.Platform.ClientCalls);
        Assert.Null(fixture.Platform.WebClient);
    }

    /// <summary>手動起動だけの旧導入に対応を加えても名前・ポート・表示設定を維持します。</summary>
    /// <returns>旧版からの移行と再利用の検証です。</returns>
    [Fact]
    public async Task LegacyManualClientGainsStartupWithoutLosingItsSettings()
    {
        using SetupFixture fixture = new();
        await fixture.InitializeAsync();
        InstallationSettings settings = fixture.Settings with { WebClient = new(true, AvailablePort()), Appearance = new("Saved", "Custom") };
        SetupResult installed = await fixture.RunAsync(new(Guid.NewGuid(), ESetupAction.Install, settings), "install");
        WebClientInstallation client = Assert.IsType<WebClientInstallation>(installed.WebClient);
        WebClientInstallation legacy = client with { AutomaticStartup = false };
        await InstallationFiles.WriteAsync(Path.Combine(client.Root, WebClientCatalog.RecordName), legacy);
        await fixture.Platform.RegisterWebClientAsync(legacy);
        string appearance = await File.ReadAllTextAsync(Path.Combine(client.Root, "config", "appearance.json"));
        fixture.Platform.ClientCalls.Clear();
        WebClientSetupJob job = new(Guid.NewGuid(), settings with { WebClient = new(false, client.Port, client.Root), Appearance = new("Ignored", "Ignored") });
        WebClientSetup setup = new(fixture.Platform);
        string journal = Path.Combine(fixture.Root, "second");
        SetupResult reused = await setup.ExecuteAsync(job, journal);
        Assert.Equal(reused, await setup.ExecuteAsync(job, journal));
        Assert.True(reused.Outcome == EOutcome.Succeeded, reused.Detail);
        Assert.Equal(client, reused.WebClient);
        Assert.Equal(appearance, await File.ReadAllTextAsync(Path.Combine(client.Root, "config", "appearance.json")));
        Assert.Equal(["unregister", "register"], fixture.Platform.ClientCalls);
    }

    /// <summary>テスト用の空きポートを取得します。</summary>
    /// <returns>OSが選んだポートです。</returns>
    private static int AvailablePort()
    {
        using System.Net.Sockets.TcpListener listener = new(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }
}
