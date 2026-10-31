using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using System.Security.AccessControl;
using System.Security.Principal;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Installer.Tests;

/// <summary>停止直後のファイルロックを待ち、削除可能になる前に成功を返しません。</summary>
public sealed class InstallationFileTests
{
    /// <summary>実際の配置・修復でも、起動登録に進む前に ACL を揃えます。</summary>
    /// <returns>サービス操作を代替し、ユーザー導入の実ファイル権限を確認します。</returns>
    [Fact]
    public async Task UserInstallAndRepairProtectFilesBeforeRegistration()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using SetupFixture fixture = new();
        await fixture.InitializeAsync();
        fixture.Platform.UseFilePermissions = true;
        string registerStage = "register";
        fixture.Platform.StageBehavior = stage =>
        {
            if (OperatingSystem.IsWindows() && string.Equals(stage, registerStage, StringComparison.Ordinal))
            {
                WindowsInstallationSecurity.ValidateTree(fixture.Settings.Root, perUser: true);
            }
            return Task.CompletedTask;
        };
        SetupResult installed = await fixture.RunAsync(new(Guid.NewGuid(), ESetupAction.Install, fixture.Settings), "install");
        Assert.Equal(EOutcome.Succeeded, installed.Outcome);
        string data = Path.Combine(fixture.Settings.Root, "data", "user.txt");
        await File.WriteAllTextAsync(data, "retained");
        string configuration = Path.Combine(fixture.Settings.Root, "config", "contents.toml");
        FileSecurity loose = new FileInfo(configuration).GetAccessControl();
        loose.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Modify, AccessControlType.Allow));
        new FileInfo(configuration).SetAccessControl(loose);
        InstallationRecord record = await InstallationFiles.ReadAsync(fixture.Settings.Root);
        SetupResult repaired = await fixture.RunAsync(new(Guid.NewGuid(), ESetupAction.Reinstall, record.Settings, record.InstallationId), "repair");
        Assert.Equal(EOutcome.Succeeded, repaired.Outcome);
        Assert.Equal("retained", await File.ReadAllTextAsync(data));
        await File.WriteAllTextAsync(data, "writable");
        WindowsInstallationSecurity.ValidateTree(fixture.Settings.Root, perUser: true);
    }

    /// <summary>一時的な共有違反を待ち、ロック解放後に同じファイルだけを削除します。</summary>
    /// <returns>Windowsの実ファイルロックを使う検証です。</returns>
    [Fact]
    public async Task RemovalWaitsForFileHandleRelease()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using SetupFixture fixture = new();
        await fixture.InitializeAsync();
        string name = "locked.dll";
        string path = Path.Combine(fixture.Root, name);
        await File.WriteAllTextAsync(path, "fixture");
        using FileStream held = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Task deleting = InstallationFiles.DeleteFileAsync(fixture.Root, name);
        try
        {
            await Task.Delay(InstallationDefaults.PollInterval);
            Assert.False(deleting.IsCompleted);
            Assert.True(File.Exists(path));
        }
        finally
        {
            await held.DisposeAsync().ConfigureAwait(true);
            await deleting.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
        await deleting.ConfigureAwait(true);
        Assert.False(File.Exists(path));
    }

    /// <summary>削除・記録移動の範囲を限定し、導入ルート自体や範囲外へは作用しません。</summary>
    /// <returns>境界外ファイルの保持と消失済み対象の扱いの確認です。</returns>
    [Fact]
    public async Task CleanupRejectsRootAndOutsidePathsAsync()
    {
        using SetupFixture fixture = new();
        await fixture.InitializeAsync();
        string root = Directory.CreateDirectory(Path.Combine(fixture.Root, "owned")).FullName;
        string outside = Directory.CreateDirectory(Path.Combine(fixture.Root, "outside")).FullName;
        await File.WriteAllTextAsync(Path.Combine(root, "payload.txt"), "owned");
        await File.WriteAllTextAsync(Path.Combine(outside, "payload.txt"), "outside");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => InstallationFiles.DeleteDirectoryAsync(root, "."));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => InstallationFiles.DeleteDirectoryAsync(root, Path.Combine("..", "outside")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => InstallationFiles.DeleteFileAsync(root, Path.Combine("..", "outside", "payload.txt")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => InstallationFiles.MoveFileAsync(root, "payload.txt", Path.Combine("..", "outside", "moved.txt")));
        await InstallationFiles.DeleteDirectoryAsync(root, "already-removed");
        Assert.Equal("owned", await File.ReadAllTextAsync(Path.Combine(root, "payload.txt")));
        Assert.Equal("outside", await File.ReadAllTextAsync(Path.Combine(outside, "payload.txt")));
    }
}
