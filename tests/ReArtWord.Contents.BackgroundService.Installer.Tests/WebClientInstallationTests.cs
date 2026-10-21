using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Installer.Tests;

/// <summary>任意導入、既存利用と独立した削除を実ファイルで確認します。</summary>
public sealed class WebClientInstallationTests
{
    /// <summary>導入しない選択ではclient本体や起動入口を作りません。</summary>
    /// <returns>Coreだけの導入と再配置の検証です。</returns>
    [Fact]
    public async Task SkippingClientOmitsItsPayloadAndRecovery()
    {
        using SetupFixture fixture = new();
        await fixture.InitializeAsync();
        InstallationSettings settings = fixture.Settings with { WebClient = new(false, WebClientSettings.DefaultPort) };
        SetupResult installed = await fixture.RunAsync(new(Guid.NewGuid(), ESetupAction.Install, settings), "install");
        Assert.Equal(EOutcome.Succeeded, installed.Outcome);
        Assert.Null(installed.WebClient);
        InstallationRecord record = await InstallationFiles.ReadAsync(settings.Root);
        Assert.Null(record.WebClient);
        Assert.Null(fixture.Platform.WebClient);
        Assert.False(Directory.Exists(fixture.Platform.ClientRoot));
        Assert.False(Directory.Exists(Path.Combine(settings.Root, "client")));
        Assert.False(Directory.Exists(Path.Combine(settings.Root, "recovery", "client")));
        Assert.False(File.Exists(Path.Combine(settings.Root, "Open-Client.cmd")));
        SetupResult repaired = await fixture.RunAsync(new(Guid.NewGuid(), ESetupAction.Reinstall, record.Settings, record.InstallationId), "repair");
        Assert.Equal(EOutcome.Succeeded, repaired.Outcome);
        Assert.Null(fixture.Platform.WebClient);
    }

    /// <summary>同じPCの追加導入は既存clientとportを引き継ぎ、Core削除後も保持します。</summary>
    /// <returns>共有配置と独立した保持の検証です。</returns>
    [Fact]
    public async Task ExistingClientIsReusedAndSurvivesCoreRemoval()
    {
        using SetupFixture fixture = new();
        await fixture.InitializeAsync();
        InstallationSettings settings = fixture.Settings with { WebClient = new(true, AvailablePort()), Appearance = new("Fixture title", "Fixture subtitle") };
        SetupJob firstJob = new(Guid.NewGuid(), ESetupAction.Install, settings);
        SetupResult installed = await fixture.RunAsync(firstJob, "install");
        Assert.Equal(EOutcome.Succeeded, installed.Outcome);
        InstallationRecord first = await InstallationFiles.ReadAsync(settings.Root);
        WebClientInstallation client = Assert.IsType<WebClientInstallation>(first.WebClient);
        Assert.Equal(settings.WebClient.Port, client.Port);
        Assert.Equal(client, installed.WebClient);
        Assert.Equal(client, (await fixture.RunAsync(firstJob, "install")).WebClient);
        Assert.Equal(fixture.Platform.ClientRoot, client.Root);
        Assert.Equal(client, await WebClientCatalog.ReadAsync(client.Root));
        Assert.False(Directory.Exists(Path.Combine(first.Settings.Root, "client")));
        string appearance = await File.ReadAllTextAsync(Path.Combine(client.Root, "config", "appearance.json"));
        string executable = Path.Combine(WebClientCatalog.ProgramRoot(client), client.EntryPoint);
        DateTimeOffset written = File.GetLastWriteTimeUtc(executable);
        InstallationSettings secondSettings = settings with { Root = Path.Combine(fixture.Root, "second"), WebClient = new(true, AvailablePort()), Appearance = new("Ignored", "Ignored") };
        SetupResult reused = await fixture.RunAsync(new(Guid.NewGuid(), ESetupAction.Install, secondSettings), "second-install");
        Assert.Equal(EOutcome.Succeeded, reused.Outcome);
        Assert.Equal(client, reused.WebClient);
        Assert.Equal(client, ContractJson.Copy(reused).WebClient);
        InstallationRecord second = await InstallationFiles.ReadAsync(secondSettings.Root);
        Assert.Equal(client, second.WebClient);
        Assert.False(second.Settings.WebClient!.Install);
        Assert.Equal(written, File.GetLastWriteTimeUtc(executable));
        Assert.Equal(appearance, await File.ReadAllTextAsync(Path.Combine(client.Root, "config", "appearance.json")));
        Assert.False(Directory.Exists(Path.Combine(secondSettings.Root, "client")));
        SetupResult removed = await fixture.RunAsync(new(Guid.NewGuid(), ESetupAction.Uninstall, first.Settings, first.InstallationId, false, false), "remove-core");
        Assert.Equal(EOutcome.Succeeded, removed.Outcome);
        Assert.Equal(client, await WebClientCatalog.ReadAsync(client.Root));
        Assert.True(File.Exists(executable));
        Assert.True(File.Exists(Path.Combine(secondSettings.Root, "Open-Client.cmd")));
    }

    /// <summary>clientだけを削除し、保持設定を再導入へ引き継ぎます。</summary>
    /// <returns>削除の独立性、同一操作の再実行防止と再導入の検証です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovingClientKeepsCoreAndHonorsConfiguration(bool keepConfiguration)
    {
        using SetupFixture fixture = new();
        await fixture.InitializeAsync();
        InstallationSettings settings = fixture.Settings with { WebClient = new(true, AvailablePort()), Appearance = new("Saved title", "Saved subtitle") };
        Assert.Equal(EOutcome.Succeeded, (await fixture.RunAsync(new(Guid.NewGuid(), ESetupAction.Install, settings), "install")).Outcome);
        WebClientInstallation client = fixture.Platform.WebClient!;
        fixture.Platform.Calls.Clear();
        WebClientRemovalJob job = new(Guid.NewGuid(), client, keepConfiguration);
        WebClientRemoval remover = new(fixture.Platform);
        string journal = Path.Combine(fixture.Root, "remove-client");
        SetupResult removed = await remover.ExecuteAsync(job, journal);
        Assert.Equal(EOutcome.Succeeded, removed.Outcome);
        Assert.Equal(removed, await remover.ExecuteAsync(job, journal));
        Assert.Equal(EOutcome.NotExecuted, (await remover.ExecuteAsync(job with { KeepConfiguration = !keepConfiguration }, journal)).Outcome);
        Assert.Empty(fixture.Platform.Calls);
        Assert.Null(fixture.Platform.WebClient);
        Assert.True(File.Exists(Path.Combine(settings.Root, "core", "core.exe")));
        Assert.False(File.Exists(Path.Combine(WebClientCatalog.ProgramRoot(client), client.EntryPoint)));
        Assert.Equal(keepConfiguration, File.Exists(Path.Combine(client.Root, "config", "appearance.json")));
        Assert.Equal(client, await WebClientCatalog.ReadRemovedAsync(client.Root));
        await Assert.ThrowsAsync<FileNotFoundException>(() => WebClientCatalog.ReadAsync(client.Root));
        InstallationSettings next = settings with { Root = Path.Combine(fixture.Root, "next"), Appearance = new() };
        SetupResult installed = await fixture.RunAsync(new(Guid.NewGuid(), ESetupAction.Install, next), "reinstall-client");
        Assert.True(installed.Outcome == EOutcome.Succeeded, installed.Detail);
        WebClientInstallation replacement = (await InstallationFiles.ReadAsync(next.Root)).WebClient!;
        Assert.Equal(client.InstallationId, replacement.InstallationId);
        ClientAppearance appearance = JsonSerializer.Deserialize<ClientAppearance>(await File.ReadAllTextAsync(Path.Combine(client.Root, "config", "appearance.json")), ContractJson.Options)!;
        string expectedTitle = ClientAppearance.DefaultTitle;
        if (keepConfiguration)
        {
            expectedTitle = "Saved title";
        }
        Assert.Equal(expectedTitle, appearance.Title ?? ClientAppearance.DefaultTitle);
    }

    /// <summary>所有が変わったclientには停止も削除も行いません。</summary>
    /// <returns>誤対象拒否の検証です。</returns>
    [Fact]
    public async Task ChangedClientAndConcurrentRemovalLeaveFilesIntact()
    {
        using SetupFixture fixture = new();
        await fixture.InitializeAsync();
        InstallationSettings settings = fixture.Settings with { WebClient = new(true, AvailablePort()) };
        Assert.Equal(EOutcome.Succeeded, (await fixture.RunAsync(new(Guid.NewGuid(), ESetupAction.Install, settings), "install")).Outcome);
        WebClientInstallation client = fixture.Platform.WebClient!;
        WebClientRemoval remover = new(fixture.Platform);
        SetupResult changed = await remover.ExecuteAsync(new(Guid.NewGuid(), client with { InstallationId = Guid.NewGuid() }, false), Path.Combine(fixture.Root, "wrong-client"));
        Assert.Equal(EOutcome.Failed, changed.Outcome);
        using FileStream held = WebClientCatalog.AcquireLock(client.Root);
        SetupResult concurrent = await remover.ExecuteAsync(new(Guid.NewGuid(), client, false), Path.Combine(fixture.Root, "locked-client"));
        Assert.Equal(EOutcome.Failed, concurrent.Outcome);
        Assert.True(File.Exists(Path.Combine(WebClientCatalog.ProgramRoot(client), client.EntryPoint)));
        Assert.Equal(client, fixture.Platform.WebClient);
    }

    /// <summary>従来の配置もCore削除後に起動可能な独立clientとして残します。</summary>
    /// <returns>旧配置の移行と明示削除の検証です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyClientCanBeRetainedThenRemoved(bool keepConfiguration)
    {
        using SetupFixture fixture = new();
        await fixture.InitializeAsync();
        Assert.Equal(EOutcome.Succeeded, (await fixture.RunAsync(new(Guid.NewGuid(), ESetupAction.Install, fixture.Settings), "install")).Outcome);
        InstallationRecord record = await InstallationFiles.ReadAsync(fixture.Settings.Root);
        WebClientInstallation before = await WebClientCatalog.ReadAsync(Path.Combine(record.Settings.Root, "client"));
        SetupResult removed = await fixture.RunAsync(new(Guid.NewGuid(), ESetupAction.Uninstall, record.Settings, record.InstallationId, false, false), "remove-core");
        Assert.Equal(EOutcome.Succeeded, removed.Outcome);
        Assert.Equal(before, await WebClientCatalog.ReadAsync(before.Root));
        Assert.True(File.Exists(Path.Combine(before.Root, "Open-Client.cmd")));
        Assert.True(File.Exists(Path.Combine(before.Root, "config", "appearance.json")));
        SetupResult clientRemoved = await new WebClientRemoval(fixture.Platform).ExecuteAsync(new(Guid.NewGuid(), before, keepConfiguration), Path.Combine(fixture.Root, "remove-client"));
        Assert.Equal(EOutcome.Succeeded, clientRemoved.Outcome);
        Assert.False(File.Exists(Path.Combine(WebClientCatalog.ProgramRoot(before), before.EntryPoint)));
        Assert.Equal(keepConfiguration, File.Exists(Path.Combine(record.Settings.Root, "config", "appearance.json")));
        Assert.Equal(keepConfiguration, File.Exists(Path.Combine(before.Root, "config", "appearance.json")));
        Assert.False(File.Exists(Path.Combine(record.Settings.Root, "Open-Client.cmd")));
    }

    /// <summary>使用中のTCP portはCoreを作成する前に拒否します。</summary>
    /// <returns>衝突時の無副作用の確認です。</returns>
    [Fact]
    public async Task OccupiedClientPortFailsBeforeInstallingCore()
    {
        using SetupFixture fixture = new();
        await fixture.InitializeAsync();
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Assert.False(InstallationPolicy.ClientPortAvailable(port));
        SetupResult result = await fixture.RunAsync(new(Guid.NewGuid(), ESetupAction.Install, fixture.Settings with { WebClient = new(true, port) }), "occupied");
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Empty(fixture.Platform.Calls);
        Assert.False(Directory.Exists(fixture.Settings.Root));
        listener.Stop();
        Assert.True(InstallationPolicy.ClientPortAvailable(port));
    }

    /// <summary>この試験中だけ使用する空きportを選択します。</summary>
    /// <returns>OSが割り当てたportです。</returns>
    private static int AvailablePort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
