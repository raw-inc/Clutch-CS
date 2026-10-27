using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Installer.Tests;

/// <summary>停止後のファイル占有と削除途中の中断を、実ファイルで検証します。</summary>
public sealed class SetupRemovalFileTests
{
    /// <summary>各削除対象の一時占有が解消すれば、一度の削除操作を完了します。</summary>
    /// <returns>対象の削除と操作記録の確認です。</returns>
    [Theory]
    [InlineData("core/core.exe")]
    [InlineData("updater/updater.exe")]
    [InlineData("cli/cli.exe")]
    [InlineData("staging/payload.dll")]
    [InlineData("backups/previous/core.exe")]
    [InlineData("recovery/core/core.exe")]
    [InlineData("data/state.db")]
    [InlineData("config/contents.toml")]
    [InlineData("client-state/profile.json")]
    [InlineData("Open-Client.cmd")]
    [InlineData("open-client.sh")]
    [InlineData("installation.json")]
    public async Task RemovalWaitsForReleasedFileAsync(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using SetupFixture fixture = new();
        InstallationRecord record = await InstallAsync(fixture);
        string path = Path.Combine(record.Settings.Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
        {
            await File.WriteAllTextAsync(path, "owned file");
        }
        SetupJob job = new(Guid.NewGuid(), ESetupAction.Uninstall, record.Settings, record.InstallationId, KeepDatabase: false, KeepConfiguration: false);
        SetupResult result = await InstallationFileLock.RunAsync(Path.GetDirectoryName(path)!, Path.GetFileName(path), reader => reader.Dispose(),
            () => fixture.RunAsync(job, "remove"));
        Assert.True(result.Outcome == EOutcome.Succeeded, result.Detail);
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(Path.Combine(record.Settings.Root, InstallationFiles.RecordName)));
        Assert.True(File.Exists(Path.Combine(record.Settings.Root, "uninstalled.json")));
        Assert.Equal(["stop", "unregister"], fixture.Platform.Calls);
        Assert.Equal(result, await fixture.RunAsync(job, "remove"));
        Assert.Equal(["stop", "unregister"], fixture.Platform.Calls);
    }

    /// <summary>占有待機を中断した場合は完了記録を作らず、同じ操作を再実行しません。</summary>
    /// <returns>未削除ファイルと中断結果の保持です。</returns>
    [Theory]
    [InlineData("core/core.exe")]
    [InlineData("config/contents.toml")]
    [InlineData("Open-Client.cmd")]
    public async Task CancellationPreservesIncompleteRemovalAsync(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using SetupFixture fixture = new();
        InstallationRecord record = await InstallAsync(fixture);
        string path = Path.Combine(record.Settings.Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            await File.WriteAllTextAsync(path, "retained file");
        }
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(45));
        SetupJob job = new(Guid.NewGuid(), ESetupAction.Uninstall, record.Settings, record.InstallationId, KeepDatabase: false, KeepConfiguration: false);
        SetupResult result = await InstallationFileLock.RunAsync(Path.GetDirectoryName(path)!, Path.GetFileName(path), _ => cancellation.Cancel(),
            () => fixture.RunAsync(job, "remove", cancellation.Token));
        Assert.Equal(EOutcome.Unknown, result.Outcome);
        Assert.Equal("remove", result.Stage);
        Assert.True(File.Exists(path));
        Assert.True(File.Exists(Path.Combine(record.Settings.Root, InstallationFiles.RecordName)));
        Assert.False(File.Exists(Path.Combine(record.Settings.Root, "uninstalled.json")));
        Assert.Equal(result, await fixture.RunAsync(job, "remove"));
        Assert.Equal(["stop", "unregister"], fixture.Platform.Calls);
    }

    /// <summary>一部が削除済みでも、残ったファイルの解放後に削除を完了します。</summary>
    /// <returns>削除の途中状態を成功扱いにせず処理する確認です。</returns>
    [Fact]
    public async Task RemovalCompletesAfterPartialCleanupAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using SetupFixture fixture = new();
        InstallationRecord record = await InstallAsync(fixture);
        string core = Path.Combine(record.Settings.Root, "core");
        string removed = Path.Combine(core, "already-removed.txt");
        await File.WriteAllTextAsync(removed, "partial cleanup");
        SetupResult result = await InstallationFileLock.RunAsync(core, "core.exe", reader =>
        {
            File.Delete(removed);
            reader.Dispose();
        }, () => fixture.RunAsync(new(Guid.NewGuid(), ESetupAction.Uninstall, record.Settings, record.InstallationId), "remove"));
        Assert.True(result.Outcome == EOutcome.Succeeded, result.Detail);
        Assert.False(Directory.Exists(core));
        Assert.True(Directory.Exists(Path.Combine(record.Settings.Root, "data")));
        Assert.True(Directory.Exists(Path.Combine(record.Settings.Root, "config")));
    }

    /// <summary>占有が解消しなければ期限内に失敗し、対象とWindowsのエラー番号を残します。</summary>
    /// <returns>未削除ファイル、元の所有記録と失敗診断の確認です。</returns>
    [Fact]
    public async Task PersistentOccupationRetainsFilesAndReportsPathAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using SetupFixture fixture = new();
        InstallationRecord record = await InstallAsync(fixture);
        string core = Path.Combine(record.Settings.Root, "core");
        using FileStream reader = new(Path.Combine(core, "core.exe"), FileMode.Open, FileAccess.Read, FileShare.Read);
        using CancellationTokenSource timeout = new(InstallationDefaults.StopTimeout + TimeSpan.FromSeconds(15));
        SetupResult result = await fixture.RunAsync(new(Guid.NewGuid(), ESetupAction.Uninstall, record.Settings, record.InstallationId), "remove", timeout.Token);
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Equal("remove", result.Stage);
        Assert.Contains(core, result.Detail, StringComparison.Ordinal);
        Assert.Contains("0x80070020", result.Detail, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(core, "core.exe")));
        Assert.True(File.Exists(Path.Combine(record.Settings.Root, InstallationFiles.RecordName)));
        Assert.False(File.Exists(Path.Combine(record.Settings.Root, "uninstalled.json")));
        Assert.False(timeout.IsCancellationRequested);
    }

    /// <summary>OS登録を変更せず、Coreだけの導入を作ります。</summary>
    /// <returns>削除対象の導入記録です。</returns>
    private static async Task<InstallationRecord> InstallAsync(SetupFixture fixture)
    {
        await fixture.InitializeAsync().ConfigureAwait(false);
        InstallationSettings settings = fixture.Settings with { WebClient = new(false, WebClientSettings.DefaultPort) };
        SetupResult installed = await fixture.RunAsync(new(Guid.NewGuid(), ESetupAction.Install, settings), "install").ConfigureAwait(false);
        Assert.Equal(EOutcome.Succeeded, installed.Outcome);
        fixture.Platform.Calls.Clear();
        return await InstallationFiles.ReadAsync(settings.Root).ConfigureAwait(false);
    }
}
