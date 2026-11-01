using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Local;
using ReArtWord.Contents.BackgroundService.Tests.Shared;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Installer.Tests;

/// <summary>修復時の退避をWindowsの実ファイル占有で検証します。</summary>
public sealed class SetupRepairFileTests
{
    /// <summary>停止後に残った占有が解消すれば、各実行物を退避して修復を完了します。</summary>
    /// <returns>退避内容と再配置内容の確認です。</returns>
    [WindowsTheory]
    [InlineData("core")]
    [InlineData("updater")]
    [InlineData("client")]
    [InlineData("cli")]
    public async Task RepairWaitsForReleasedComponentAsync(string component)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using SetupFixture fixture = new();
        InstallationRecord record = await InstallAsync(fixture).ConfigureAwait(true);
        string directory = Path.Combine(record.Settings.Root, component);
        string name = component + ".exe";
        await File.WriteAllTextAsync(Path.Combine(directory, name), "damaged").ConfigureAwait(true);
        SetupJob job = new(Guid.NewGuid(), ESetupAction.Reinstall, record.Settings, record.InstallationId);
        SetupResult result = await InstallationFileLock.RunAsync(directory, name, reader => reader.Dispose(),
            () => fixture.RunAsync(job, "repair")).ConfigureAwait(true);
        Assert.True(result.Outcome == EOutcome.Succeeded, result.Detail);
        Assert.Equal("fixture:" + component, await File.ReadAllTextAsync(Path.Combine(directory, name)).ConfigureAwait(true));
        Assert.Equal("damaged", await File.ReadAllTextAsync(Path.Combine(record.Settings.Root, "backups", "setup-" + job.OperationId.ToString("N"), component, name)).ConfigureAwait(true));
        Assert.Equal(1, fixture.Platform.Calls.Count(value => string.Equals(value, "stop", StringComparison.Ordinal)));
    }

    /// <summary>占有が続く場合は退避失敗を記録し、同じ操作を再実行しません。</summary>
    /// <returns>元の配置・操作結果の保持と、後続処理がないことの確認です。</returns>
    [WindowsFact]
    public async Task RepairRetainsLockedComponentAndDoesNotReplayAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using SetupFixture fixture = new();
        InstallationRecord record = await InstallAsync(fixture).ConfigureAwait(true);
        string directory = Path.Combine(record.Settings.Root, "core");
        SetupJob job = new(Guid.NewGuid(), ESetupAction.Reinstall, record.Settings, record.InstallationId);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        SetupResult result;
        using (FileStream reader = new(Path.Combine(directory, "core.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = await fixture.RunAsync(job, "repair", timeout.Token).ConfigureAwait(true);
        }
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Equal("backup", result.Stage);
        Assert.Equal("fixture:core", await File.ReadAllTextAsync(Path.Combine(directory, "core.exe")).ConfigureAwait(true));
        Assert.False(Directory.Exists(Path.Combine(record.Settings.Root, "backups", "setup-" + job.OperationId.ToString("N"), "core")));
        Assert.Equal(result, await fixture.RunAsync(job, "repair", timeout.Token).ConfigureAwait(true));
        Assert.Equal(["stop"], fixture.Platform.Calls);
    }

    /// <summary>ファイル占有待ちのキャンセル後に、退避や起動を進めません。</summary>
    /// <returns>中断結果と元の配置の確認です。</returns>
    [WindowsFact]
    public async Task RepairCancellationRetainsComponentAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using SetupFixture fixture = new();
        InstallationRecord record = await InstallAsync(fixture).ConfigureAwait(true);
        string directory = Path.Combine(record.Settings.Root, "core");
        SetupJob job = new(Guid.NewGuid(), ESetupAction.Reinstall, record.Settings, record.InstallationId);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        SetupResult result = await InstallationFileLock.RunAsync(directory, "core.exe", _ => cancellation.Cancel(),
            () => fixture.RunAsync(job, "repair", cancellation.Token)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Unknown, result.Outcome);
        Assert.Equal("backup", result.Stage);
        Assert.Equal("fixture:core", await File.ReadAllTextAsync(Path.Combine(directory, "core.exe")).ConfigureAwait(true));
        Assert.Equal(["stop"], fixture.Platform.Calls);
    }

    /// <summary>修復元の更新にも同じファイル占有待機を適用します。</summary>
    /// <returns>新しい修復元を欠落なく保存することの確認です。</returns>
    [WindowsFact]
    public async Task RecoveryRefreshWaitsForReleasedFilesAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using SetupFixture fixture = new();
        InstallationRecord record = await InstallAsync(fixture).ConfigureAwait(true);
        string directory = Path.Combine(record.Settings.Root, "recovery", "core");
        string core = Path.Combine(record.Settings.Root, "core");
        Contracts.Updates.Releases.ReleaseManifest manifest = (await LocalUpdateExecutor.ReadManifestAsync(core).ConfigureAwait(true)) with { ReleaseId = "fixture-v2" };
        await InstallationFileLock.RunAsync(directory, "core.exe", reader => reader.Dispose(), async () =>
        {
            await InstallationRecovery.RefreshCoreAsync(core, manifest).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(true);
        Assert.Equal("fixture-v2", (await LocalUpdateExecutor.ReadManifestAsync(directory).ConfigureAwait(true)).ReleaseId);
        Assert.Equal("fixture:core", await File.ReadAllTextAsync(Path.Combine(directory, "core.exe")).ConfigureAwait(true));
    }

    /// <summary>WebClientの再配置でも、停止後の占有解消を待って設定を引き継ぎます。</summary>
    /// <returns>同じ導入の保持と一度だけの停止確認です。</returns>
    [WindowsFact]
    public async Task ClientReinstallWaitsForReleasedFilesAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using SetupFixture fixture = new();
        InstallationRecord core = await InstallAsync(fixture).ConfigureAwait(true);
        InstallationRecord record = core with { Settings = core.Settings with { WebClient = new(true, 49991), Appearance = new("Saved title", "Saved subtitle") } };
        WebClientInstaller installer = new(fixture.Platform);
        WebClientInstallation client = (await installer.InstallAsync(record).ConfigureAwait(true))!;
        string appearancePath = Path.Combine(client.Root, "config", "appearance.json");
        string appearance = await File.ReadAllTextAsync(appearancePath).ConfigureAwait(true);
        await InstallationFiles.WriteAsync(Path.Combine(client.Root, WebClientCatalog.RecordName), client with { AutomaticStartup = false }).ConfigureAwait(true);
        WebClientInstallation? replacement = await InstallationFileLock.RunAsync(client.Root, Path.Combine("app", client.EntryPoint), reader => reader.Dispose(),
            () => installer.InstallAsync(record)).ConfigureAwait(true);
        Assert.NotNull(replacement);
        Assert.Equal(client.InstallationId, replacement.InstallationId);
        Assert.True(replacement.AutomaticStartup);
        Assert.Equal(appearance, await File.ReadAllTextAsync(appearancePath).ConfigureAwait(true));
        Assert.Equal(1, fixture.Platform.ClientStops);
    }

    /// <summary>再同期時のDB退避も、解放後の同じファイルを移動します。</summary>
    /// <returns>DB内容の保持と元の配置の退避確認です。</returns>
    [WindowsFact]
    public async Task DataBackupWaitsForReleasedFilesAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using SetupFixture fixture = new();
        InstallationRecord record = await InstallAsync(fixture).ConfigureAwait(true);
        string directory = Path.Combine(record.Settings.Root, "data");
        string backup = Path.Combine(record.Settings.Root, "backups", "saved-data");
        await File.WriteAllTextAsync(Path.Combine(directory, "state.db"), "retained database").ConfigureAwait(true);
        await InstallationFileLock.RunAsync(directory, "state.db", reader => reader.Dispose(), async () =>
        {
            await InstallationFiles.MoveDirectoryAsync(record.Settings.Root, directory, backup).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(true);
        Assert.False(Directory.Exists(directory));
        Assert.Equal("retained database", await File.ReadAllTextAsync(Path.Combine(backup, "state.db")).ConfigureAwait(true));
    }

    /// <summary>所有範囲外・導入ルート自体・既存の退避先へは移動しません。</summary>
    /// <returns>両方の配置の内容を維持することの確認です。</returns>
    [Fact]
    public async Task BackupRejectsUnownedPathsAndExistingDestinationAsync()
    {
        using SetupFixture fixture = new();
        InstallationRecord record = await InstallAsync(fixture);
        string source = Path.Combine(record.Settings.Root, "core");
        string outside = Path.Combine(fixture.Root, "outside");
        string backup = Directory.CreateDirectory(Path.Combine(record.Settings.Root, "backups", "existing")).FullName;
        await File.WriteAllTextAsync(Path.Combine(backup, "core.exe"), "earlier backup");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => InstallationFiles.MoveDirectoryAsync(record.Settings.Root, source, outside));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => InstallationFiles.MoveDirectoryAsync(record.Settings.Root, record.Settings.Root, backup));
        await Assert.ThrowsAsync<IOException>(() => InstallationFiles.MoveDirectoryAsync(record.Settings.Root, source, backup));
        Assert.Equal("fixture:core", await File.ReadAllTextAsync(Path.Combine(source, "core.exe")));
        Assert.Equal("earlier backup", await File.ReadAllTextAsync(Path.Combine(backup, "core.exe")));
        Assert.False(Directory.Exists(outside));
    }

    /// <summary>OS登録を変更せず、実ファイルと導入記録を作ります。</summary>
    /// <returns>修復対象の導入記録です。</returns>
    private static async Task<InstallationRecord> InstallAsync(SetupFixture fixture)
    {
        await fixture.InitializeAsync().ConfigureAwait(false);
        SetupResult installed = await fixture.RunAsync(new(Guid.NewGuid(), ESetupAction.Install, fixture.Settings), "install").ConfigureAwait(false);
        Assert.Equal(EOutcome.Succeeded, installed.Outcome);
        fixture.Platform.Calls.Clear();
        return await InstallationFiles.ReadAsync(fixture.Settings.Root).ConfigureAwait(false);
    }

}
