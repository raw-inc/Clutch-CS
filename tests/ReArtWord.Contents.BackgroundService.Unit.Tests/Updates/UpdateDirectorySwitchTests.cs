using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Local;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Updates;

/// <summary>更新時のディレクトリ切替を、実ファイルの占有と既存の退避先で検証します。</summary>
public sealed class UpdateDirectorySwitchTests : IDisposable
{
    private readonly string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "clutch-update-switch-" + Guid.NewGuid().ToString("N"))).FullName;

    /// <summary>Windowsがアクセス拒否を返す一時的なファイル占有の解消後に切り替えます。</summary>
    /// <returns>待機後もファイルの内容を保持した切替の確認です。</returns>
    [Fact]
    public async Task OpenFileReleasedBeforeRetryAllowsDirectoryMoveAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        string source = Directory.CreateDirectory(Path.Combine(root, "installed")).FullName;
        string destination = Path.Combine(root, "backup");
        string file = Path.Combine(source, "payload.txt");
        await File.WriteAllTextAsync(file, "installed release").ConfigureAwait(true);
        using FileStream reader = new(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        int attempts = 0;
        await UpdateDirectorySwitch.MoveAsync(source, destination, path =>
        {
            if (string.Equals(path, source, StringComparison.Ordinal) && ++attempts == 2)
            {
                reader.Dispose();
            }
        }).ConfigureAwait(true);
        Assert.False(Directory.Exists(source));
        Assert.Equal("installed release", await File.ReadAllTextAsync(Path.Combine(destination, "payload.txt")).ConfigureAwait(true));
    }

    /// <summary>占有が解消しない場合は有限時間で失敗し、元の配置を保持します。</summary>
    /// <returns>切替未適用と元ファイルの保持の確認です。</returns>
    [Fact]
    public async Task PersistentlyOpenFilePreservesInstallationAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        string source = Directory.CreateDirectory(Path.Combine(root, "installed")).FullName;
        string destination = Path.Combine(root, "backup");
        string file = Path.Combine(source, "payload.txt");
        await File.WriteAllTextAsync(file, "installed release").ConfigureAwait(true);
        using FileStream reader = new(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<IOException>(() => UpdateDirectorySwitch.MoveAsync(source, destination, static _ =>
        {
        }, timeout.Token)).ConfigureAwait(true);
        Assert.False(Directory.Exists(destination));
        Assert.Equal("installed release", await File.ReadAllTextAsync(file).ConfigureAwait(true));
    }

    /// <summary>既存の退避先を上書きせず、両方の配置を保持します。</summary>
    /// <returns>退避先の衝突を失敗として扱う確認です。</returns>
    [Fact]
    public async Task ExistingBackupIsNotOverwrittenAsync()
    {
        string source = Directory.CreateDirectory(Path.Combine(root, "installed")).FullName;
        string destination = Directory.CreateDirectory(Path.Combine(root, "backup")).FullName;
        await File.WriteAllTextAsync(Path.Combine(source, "payload.txt"), "installed release").ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(destination, "payload.txt"), "previous release").ConfigureAwait(true);
        await Assert.ThrowsAsync<IOException>(() => UpdateDirectorySwitch.MoveAsync(source, destination, static _ =>
        {
        })).ConfigureAwait(true);
        Assert.Equal("installed release", await File.ReadAllTextAsync(Path.Combine(source, "payload.txt")).ConfigureAwait(true));
        Assert.Equal("previous release", await File.ReadAllTextAsync(Path.Combine(destination, "payload.txt")).ConfigureAwait(true));
    }

    /// <summary>この試験専用の配置だけを削除します。</summary>
    public void Dispose() => Directory.Delete(root, true);
}
