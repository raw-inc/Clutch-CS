using System.Net;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;
using ReArtWord.Contents.BackgroundService.Tests.Shared;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Installer.Tests;

/// <summary>WebClientの削除対象と完了記録を、Windowsの実ファイル占有で検証します。</summary>
public sealed class WebClientRemovalFileTests
{
    /// <summary>実行物以外の占有も待ち、中断時には削除完了の記録を作りません。</summary>
    /// <returns>ファイル削除と所有記録の保持の確認です。</returns>
    [WindowsTheory]
    [InlineData("app/client.exe", false)]
    [InlineData("app/release-manifest.json", false)]
    [InlineData("Open-Client.cmd", false)]
    [InlineData("Start-Client.ps1", false)]
    [InlineData("open-client.sh", false)]
    [InlineData("config/appearance.json", false)]
    [InlineData("state/profile.json", false)]
    [InlineData("web-client.json", false)]
    [InlineData("config/appearance.json", true)]
    [InlineData("state/profile.json", true)]
    public async Task RemovalHandlesOccupiedOwnedFilesAsync(string relativePath, bool cancel)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using SetupFixture fixture = new();
        await fixture.InitializeAsync().ConfigureAwait(true);
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        InstallationSettings settings = fixture.Settings with { WebClient = new(true, port) };
        SetupResult installed = await fixture.RunAsync(new(Guid.NewGuid(), ESetupAction.Install, settings), "install").ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, installed.Outcome);
        WebClientInstallation client = fixture.Platform.WebClient!;
        string state = WebClientStartup.StateDirectory(client);
        string path = Path.Combine(client.Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        string statePrefix = "state/";
        if (relativePath.StartsWith(statePrefix, StringComparison.Ordinal))
        {
            path = Path.Combine(state, relativePath[statePrefix.Length..]);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
        {
            await File.WriteAllTextAsync(path, "owned file").ConfigureAwait(true);
        }
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(45));
        WebClientRemoval remover = new(fixture.Platform);
        WebClientRemovalJob job = new(Guid.NewGuid(), client, false);
        string journal = Path.Combine(fixture.Root, "remove");
        try
        {
            SetupResult result = await InstallationFileLock.RunAsync(Path.GetDirectoryName(path)!, Path.GetFileName(path), reader =>
            {
                if (cancel)
                {
                    cancellation.Cancel();
                }
                else
                {
                    reader.Dispose();
                }
            }, () => remover.ExecuteAsync(job, journal, cancellation.Token)).ConfigureAwait(true);
            if (cancel)
            {
                Assert.Equal(EOutcome.Unknown, result.Outcome);
                Assert.True(File.Exists(path));
                Assert.True(File.Exists(Path.Combine(client.Root, WebClientCatalog.RecordName)));
                Assert.False(File.Exists(Path.Combine(client.Root, WebClientCatalog.RemovedRecordName)));
            }
            else
            {
                Assert.True(result.Outcome == EOutcome.Succeeded, result.Detail);
                Assert.False(File.Exists(path));
                Assert.Equal(client, await WebClientCatalog.ReadRemovedAsync(client.Root).ConfigureAwait(true));
                Assert.False(File.Exists(Path.Combine(client.Root, WebClientCatalog.RecordName)));
            }
            Assert.True(File.Exists(Path.Combine(settings.Root, "core", "core.exe")));
            Assert.Equal(1, fixture.Platform.ClientStops);
            Assert.Equal(result, await remover.ExecuteAsync(job, journal).ConfigureAwait(true));
            Assert.Equal(1, fixture.Platform.ClientStops);
        }
        finally
        {
            await InstallationFiles.DeleteDirectoryAsync(Path.GetDirectoryName(state)!, Path.GetFileName(state), CancellationToken.None).ConfigureAwait(true);
        }
    }
}
