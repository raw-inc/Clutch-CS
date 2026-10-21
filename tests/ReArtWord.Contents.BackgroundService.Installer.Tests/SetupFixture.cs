using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Local;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;

namespace ReArtWord.Contents.BackgroundService.Installer.Tests;

/// <summary>外部のファイルを使わない独立した配置試験環境です。</summary>
internal sealed class SetupFixture : IDisposable
{
    public string Root
    {
        get;
    } = Path.Combine(Path.GetTempPath(), "clutch-setup-tests-" + Guid.NewGuid().ToString("N"));

    public RecordingPlatform Platform
    {
        get;
    } = new();

    public InstallationSettings Settings
    {
        get; private set;
    } = null!;

    /// <summary>配置試験の実行境界を記録または準備します。</summary>

    /// <returns>試験用処理の完了です。</returns>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Root);
        Platform.ClientRoot = Path.Combine(Root, "shared-client");
        foreach (string component in new[] { "core", "updater", "client", "cli" })
        {
            string directory = Path.Combine(Root, "package", component);
            Directory.CreateDirectory(directory);
            byte[] content = System.Text.Encoding.UTF8.GetBytes("fixture:" + component);
            string entry = component + ".exe";
            await File.WriteAllBytesAsync(Path.Combine(directory, entry), content, cancellationToken).ConfigureAwait(true);
            ReleaseManifest manifest = new("fixture-v1", RuntimeInformation.RuntimeIdentifier, entry, [new ReleaseFile(entry, content.Length, Convert.ToHexString(SHA256.HashData(content)))], [], ExpectedBuildVersion: "fixture-v1");
            await LocalUpdateExecutor.WriteManifestAsync(manifest, directory, cancellationToken).ConfigureAwait(true);
        }
        Contracts.Management.Discovery.ClientNetworkInterface network = ObserverNetwork.GetInterfaces().First(item => item.Addresses.Any(address => string.Equals(address.Address, "127.0.0.1", StringComparison.Ordinal)));
        Settings = new(Path.Combine(Root, "installed"), Path.Combine(Root, "package"), "cluster", "host", "peer", 1000, true, true,
            new(network.InterfaceId, "127.0.0.1", "127.255.255.255", 47740), new(), true);
    }

    /// <summary>配置試験の実行境界を記録または準備します。</summary>

    /// <returns>試験用処理の完了です。</returns>
    public Task<SetupResult> RunAsync(SetupJob job, string journal, CancellationToken cancellationToken = default) => new SetupExecutor(Platform).ExecuteAsync(job, Path.Combine(Root, journal), cancellationToken);

    /// <summary>この試験で作成した専用rootだけを削除します。</summary>
    public void Dispose() => Directory.Delete(Root, true);
}
