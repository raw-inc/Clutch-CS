using System.Security.Cryptography;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Local;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Updates;

/// <summary>共有runtimeと版の異なる依存ファイルから独立したUpdaterを構成します。</summary>
public sealed class BundledUpdaterTests
{
    /// <summary>共通ファイルを再利用し、異なる依存ファイルはCoreへ混ぜず同梱版を選びます。</summary>
    /// <returns>元Coreの保持と独立helperのハッシュ検証です。</returns>
    [Fact]
    public async Task BundledVersionOverridesLegacyConfigurationAndKeepsDependenciesIsolated()
    {
        using MediaTestFiles files = new();
        UpdatesOptions options = Options(files.Root);
        string installed = options.InstallationRoot!;
        Directory.CreateDirectory(Path.Combine(installed, "updater"));
        string entry = "ReArtWord.Contents.BackgroundService.Updater";
        if (OperatingSystem.IsWindows())
        {
            entry += ".exe";
        }
        await File.WriteAllBytesAsync(Path.Combine(installed, "core.dat"), [0]);
        await File.WriteAllBytesAsync(Path.Combine(installed, "shared.dat"), [1, 2]);
        await File.WriteAllBytesAsync(Path.Combine(installed, "different.dat"), [3]);
        await File.WriteAllBytesAsync(Path.Combine(installed, "updater", entry), [4]);
        await File.WriteAllBytesAsync(Path.Combine(installed, "updater", "different.dat"), [5]);
        ReleaseManifest updater = new("new", "test", entry, [DeclaredFile(entry, [4]), DeclaredFile("shared.dat", [1, 2]), DeclaredFile("different.dat", [5])], [], "new-build");
        await LocalUpdateExecutor.WriteManifestAsync(updater, Path.Combine(installed, "updater"));
        ReleaseManifest core = new("new", "test", "core.dat", [DeclaredFile("core.dat", [0]), DeclaredFile("shared.dat", [1, 2]), DeclaredFile("different.dat", [3]),
            DeclaredFile("updater/" + entry, [4]), DeclaredFile("updater/different.dat", [5]), DeclaredFile("updater/release-manifest.json", await System.IO.File.ReadAllBytesAsync(Path.Combine(installed, "updater", "release-manifest.json")))], [], "new-build");
        await LocalUpdateExecutor.WriteManifestAsync(core, installed);
        Guid operation = Guid.NewGuid();
        string? helper = await BundledUpdater.ResolveAsync(options, operation);
        Assert.NotNull(helper);
        Assert.False(UpdateLayout.Contains(installed, helper));
        Assert.True(UpdateLayout.Contains(options.StagingRoot!, helper));
        await ReleaseVerifier.VerifyAsync(updater, Path.GetDirectoryName(helper)!);
        Assert.Equal(new byte[] { 3 }, await System.IO.File.ReadAllBytesAsync(Path.Combine(installed, "different.dat")));
        Assert.Equal(helper, await BundledUpdater.ResolveAsync(options, operation));
        await System.IO.File.WriteAllBytesAsync(helper, [9]);
        await Assert.ThrowsAsync<InvalidDataException>(() => BundledUpdater.ResolveAsync(options, operation));
    }

    /// <summary>同梱版がない旧packageでは明示Updater設定を維持します。</summary>
    /// <returns>従来packageとの互換性です。</returns>
    [Fact]
    public async Task LegacyPackageKeepsItsExplicitUpdater()
    {
        using MediaTestFiles files = new();
        UpdatesOptions options = Options(files.Root);
        Directory.CreateDirectory(options.InstallationRoot!);
        ReleaseManifest core = new("old", "test", "core.dat", [DeclaredFile("core.dat", [0])], []);
        await LocalUpdateExecutor.WriteManifestAsync(core, options.InstallationRoot!);
        Assert.Equal(options.UpdaterExecutablePath, await BundledUpdater.ResolveAsync(options, Guid.NewGuid()));
    }

    /// <summary>一回の配置試験へ独立rootを渡します。</summary>
    /// <returns>実サービス操作のない設定です。</returns>
    private static UpdatesOptions Options(string root) => new(Path.Combine(root, "installed"), Path.Combine(root, "staging"), Path.Combine(root, "backups"), Path.Combine(root, "legacy.exe"),
        InterfaceId: "test", LocalAddress: "127.0.0.1", ClusterId: "test", BroadcastAddress: "127.255.255.255");

    /// <summary>ファイル内容とマニフェストの検証値を一致させます。</summary>
    /// <returns>小さな試験用の構成の宣言です。</returns>
    private static ReleaseFile DeclaredFile(string path, byte[] bytes) => new(path, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
}
