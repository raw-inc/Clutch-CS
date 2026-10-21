using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Local;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Installer.Tests;

/// <summary>副作用前検証、保持選択と再実行防止を検証します。</summary>
public sealed class SetupExecutorTests
{
    /// <summary>起動確認の待機中は最後の工程を完了にせず、実成功後にだけ完了します。</summary>
    /// <returns>helperの進捗fileと実処理の対応の確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SetupProgressReflectsReadinessAndDoesNotHideFailure(bool fail)
    {
        using SetupFixture fixture = new();
        await fixture.InitializeAsync();
        string journal = Path.Combine(fixture.Root, "progress");
        List<OperationProgress> observed = [];
        fixture.Platform.StageBehavior = async stage =>
        {
            OperationProgress? progress = await SetupProgressJournal.ReadAsync(journal).ConfigureAwait(true);
            Assert.NotNull(progress);
            Assert.Equal(stage, progress.Stage);
            Assert.True(progress.CompletedSteps < progress.TotalSteps);
            observed.Add(progress);
            string ready = "ready";
            if (fail && string.Equals(stage, ready, StringComparison.Ordinal))
            {
                throw new IOException("Fixture readiness failed.");
            }
        };
        SetupResult result = await fixture.RunAsync(new(Guid.NewGuid(), ESetupAction.Install, fixture.Settings), "progress");
        OperationProgress final = (await SetupProgressJournal.ReadAsync(journal))!;
        Assert.Equal(["register", "start", "ready"], observed.Select(value => value.Stage));
        Assert.Equal(observed.Select(value => value.CompletedSteps).Order(), observed.Select(value => value.CompletedSteps));
        if (fail)
        {
            Assert.Equal(EOutcome.Failed, result.Outcome);
            Assert.True(final.CompletedSteps < final.TotalSteps);
        }
        else
        {
            Assert.Equal(EOutcome.Succeeded, result.Outcome);
            Assert.Equal(final.TotalSteps, final.CompletedSteps);
        }
    }

    /// <summary>配置の検証と障害境界を実ファイルで確認します。</summary>
    /// <returns>試験完了です。</returns>
    [Fact]
    public async Task InstallRequiresConsentAndVerifiedPackage()
    {
        using SetupFixture fixture = new();
        await fixture.InitializeAsync();
        SetupResult rejected = await fixture.RunAsync(new(Guid.NewGuid(), ESetupAction.Install, fixture.Settings with { LicenseAccepted = false }), "no-consent");
        Assert.Equal(EOutcome.Failed, rejected.Outcome);
        Assert.False(Directory.Exists(fixture.Settings.Root));
        await File.WriteAllTextAsync(Path.Combine(fixture.Settings.PackageRoot, "core", "core.exe"), "corrupted");
        SetupResult corrupt = await fixture.RunAsync(new(Guid.NewGuid(), ESetupAction.Install, fixture.Settings), "corrupt");
        Assert.Equal("verify-package", corrupt.Stage);
        Assert.Empty(fixture.Platform.Calls);
    }

    /// <summary>配置の検証と障害境界を実ファイルで確認します。</summary>
    /// <returns>試験完了です。</returns>
    [Fact]
    public async Task SuccessfulInstallRecordsOwnershipAndNeverReplaysSameOperation()
    {
        using SetupFixture fixture = new();
        await fixture.InitializeAsync();
        SetupJob job = new(Guid.NewGuid(), ESetupAction.Install, fixture.Settings);
        Directory.CreateDirectory(fixture.Settings.Root);
        SetupResult first = await fixture.RunAsync(job, "operation");
        Assert.Equal(EOutcome.Succeeded, first.Outcome);
        Assert.Collection(fixture.Platform.Calls, value => Assert.Equal("register", value), value => Assert.Equal("start", value), value => Assert.Equal("ready", value));
        Assert.True(File.Exists(Path.Combine(fixture.Settings.Root, "config", "contents.toml")));
        SetupResult repeated = await fixture.RunAsync(job, "operation");
        Assert.Equal(first, repeated);
        Assert.Equal(3, fixture.Platform.Calls.Count);
        SetupResult mismatch = await fixture.RunAsync(job with { KeepDatabase = false }, "operation");
        Assert.Equal(EOutcome.NotExecuted, mismatch.Outcome);
    }

    /// <summary>配置の検証と障害境界を実ファイルで確認します。</summary>
    /// <returns>試験完了です。</returns>
    [Fact]
    public async Task FailedReadinessRetainsFilesAndReportsActualStage()
    {
        using SetupFixture fixture = new();
        await fixture.InitializeAsync();
        fixture.Platform.FailReady = true;
        SetupResult result = await fixture.RunAsync(new(Guid.NewGuid(), ESetupAction.Install, fixture.Settings), "ready-failure");
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Equal("ready", result.Stage);
        Assert.True(File.Exists(Path.Combine(fixture.Settings.Root, "core", "core.exe")));
        Assert.DoesNotContain("unregister", fixture.Platform.Calls);
    }

    /// <summary>DBと設定の独立した保持指定を確認します。</summary>
    /// <returns>試験完了です。</returns>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task UninstallHonorsIndependentRetention(bool keepDatabase, bool keepConfiguration)
    {
        using SetupFixture fixture = new();
        await fixture.InitializeAsync();
        Assert.Equal(EOutcome.Succeeded, (await fixture.RunAsync(new(Guid.NewGuid(), ESetupAction.Install, fixture.Settings), "install")).Outcome);
        InstallationRecord record = await InstallationFiles.ReadAsync(fixture.Settings.Root);
        await File.WriteAllTextAsync(Path.Combine(fixture.Settings.Root, "data", "state.db"), "retained data");
        await File.WriteAllTextAsync(Path.Combine(fixture.Settings.Root, "workspace", "user.txt"), "user content");
        SetupResult result = await fixture.RunAsync(new(Guid.NewGuid(), ESetupAction.Uninstall, record.Settings, record.InstallationId, keepDatabase, keepConfiguration), "uninstall");
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(keepDatabase, File.Exists(Path.Combine(fixture.Settings.Root, "data", "state.db")));
        Assert.Equal(keepConfiguration, File.Exists(Path.Combine(fixture.Settings.Root, "config", "contents.toml")));
        Assert.True(File.Exists(Path.Combine(fixture.Settings.Root, "workspace", "user.txt")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Settings.Root, "core")));
    }

    /// <summary>配置の検証と障害境界を実ファイルで確認します。</summary>
    /// <returns>試験完了です。</returns>
    [Fact]
    public async Task WrongIdentityAndUnsafeResyncStopBeforeServiceChanges()
    {
        using SetupFixture fixture = new();
        await fixture.InitializeAsync();
        await fixture.RunAsync(new(Guid.NewGuid(), ESetupAction.Install, fixture.Settings), "install");
        InstallationRecord record = await InstallationFiles.ReadAsync(fixture.Settings.Root);
        fixture.Platform.Calls.Clear();
        SetupResult wrong = await fixture.RunAsync(new(Guid.NewGuid(), ESetupAction.Uninstall, record.Settings, Guid.NewGuid()), "wrong");
        Assert.Equal(EOutcome.Failed, wrong.Outcome);
        SetupResult unsafeResync = await fixture.RunAsync(new(Guid.NewGuid(), ESetupAction.Reinstall, record.Settings, record.InstallationId, KeepDatabase: false), "resync");
        Assert.Equal(EOutcome.Failed, unsafeResync.Outcome);
        Assert.Empty(fixture.Platform.Calls);
    }

    /// <summary>配置の検証と障害境界を実ファイルで確認します。</summary>
    /// <returns>試験完了です。</returns>
    [Fact]
    public async Task ReinstallRestoresFilesAndKeepsCustomizedConfiguration()
    {
        using SetupFixture fixture = new();
        await fixture.InitializeAsync();
        await fixture.RunAsync(new(Guid.NewGuid(), ESetupAction.Install, fixture.Settings), "install");
        InstallationRecord record = await InstallationFiles.ReadAsync(fixture.Settings.Root);
        string config = Path.Combine(fixture.Settings.Root, "config", "contents.toml");
        await File.AppendAllTextAsync(config, "\n# preserved custom setting\n");
        string before = await File.ReadAllTextAsync(config);
        await File.WriteAllTextAsync(Path.Combine(fixture.Settings.Root, "core", "core.exe"), "damaged");
        SetupResult result = await fixture.RunAsync(new(Guid.NewGuid(), ESetupAction.Reinstall, record.Settings, record.InstallationId), "repair");
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(before, await File.ReadAllTextAsync(config));
        Assert.Equal("fixture:core", await File.ReadAllTextAsync(Path.Combine(fixture.Settings.Root, "core", "core.exe")));
    }

    /// <summary>更新後の修復で古い配布物へ戻さず、更新版の保持後だけ再配置します。</summary>
    /// <returns>版不一致と修復元更新の検証完了です。</returns>
    [Fact]
    public async Task RepairUsesUpdatedRecoveryAndRejectsOlderFilesBeforeStopping()
    {
        using SetupFixture fixture = new();
        await fixture.InitializeAsync();
        await fixture.RunAsync(new(Guid.NewGuid(), ESetupAction.Install, fixture.Settings), "install");
        InstallationRecord record = await InstallationFiles.ReadAsync(fixture.Settings.Root);
        string core = Path.Combine(record.Settings.Root, "core");
        Contracts.Updates.Releases.ReleaseManifest current = (await Infrastructure.Updates.Local.LocalUpdateExecutor.ReadManifestAsync(core)) with { ReleaseId = "fixture-v2" };
        await Infrastructure.Updates.Local.LocalUpdateExecutor.WriteManifestAsync(current, core);
        fixture.Platform.Calls.Clear();
        SetupResult stale = await fixture.RunAsync(new(Guid.NewGuid(), ESetupAction.Reinstall, record.Settings, record.InstallationId, ServeResult: true), "stale");
        Assert.Equal(EOutcome.Failed, stale.Outcome);
        Assert.Empty(fixture.Platform.Calls);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "stale", "stop.request")));
        await InstallationRecovery.RefreshCoreAsync(core, current);
        await File.WriteAllTextAsync(Path.Combine(core, "core.exe"), "damaged");
        SetupResult repaired = await fixture.RunAsync(new(Guid.NewGuid(), ESetupAction.Reinstall, record.Settings, record.InstallationId), "repair-current");
        Assert.Equal(EOutcome.Succeeded, repaired.Outcome);
        Assert.Equal("fixture-v2", (await Infrastructure.Updates.Local.LocalUpdateExecutor.ReadManifestAsync(core)).ReleaseId);
        Assert.Equal("fixture:core", await File.ReadAllTextAsync(Path.Combine(core, "core.exe")));
    }

    /// <summary>別の保守操作が所有する配置へ同時変更しません。</summary>
    /// <returns>副作用なしでの拒否確認です。</returns>
    [Fact]
    public async Task ConcurrentMaintenanceDoesNotStopOrRemoveFiles()
    {
        using SetupFixture fixture = new();
        await fixture.InitializeAsync();
        await fixture.RunAsync(new(Guid.NewGuid(), ESetupAction.Install, fixture.Settings), "install");
        InstallationRecord record = await InstallationFiles.ReadAsync(fixture.Settings.Root);
        fixture.Platform.Calls.Clear();
        using FileStream held = new(Path.Combine(record.Settings.Root, "setup.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        SetupResult result = await fixture.RunAsync(new(Guid.NewGuid(), ESetupAction.Uninstall, record.Settings, record.InstallationId), "concurrent");
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Empty(fixture.Platform.Calls);
        Assert.True(Directory.Exists(Path.Combine(record.Settings.Root, "core")));
    }
}
