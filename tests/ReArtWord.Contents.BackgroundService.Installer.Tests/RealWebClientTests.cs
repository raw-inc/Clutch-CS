using System.Net;
using System.Net.Sockets;
using System.Net.Http.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Client;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;
using ReArtWord.Contents.BackgroundService.Installer.Services;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Installer.Tests;

/// <summary>配布物の実ClientHostとUpdaterで、独立した導入と削除を確認します。</summary>
public sealed class RealWebClientTests
{
    /// <summary>指定portでHTTP応答し、独立helperが試験専用のclientだけを削除します。</summary>
    /// <returns>実配布物の検証完了です。</returns>
    [RealInstallationFact]
    public async Task WindowsStandaloneWebClient()
        => await RunAsync(false).ConfigureAwait(true);

    /// <summary>サインイン中の利用者のタスクで起動し、二重起動と削除後の登録残りを確認します。</summary>
    /// <returns>実タスクと実HTTPの検証です。</returns>
    [RealInstallationFact(interactive: true)]
    public async Task WindowsUserWebClientStartup()
        => await RunAsync(true).ConfigureAwait(true);

    /// <summary>使い捨ての配布物だけを配置し、選択した起動経路から削除まで確認します。</summary>
    /// <returns>この試験が作成した導入の確認です。</returns>
    private static async Task RunAsync(bool automaticStartup, CancellationToken cancellationToken = default)
    {
        using SetupFixture fixture = new();
        await fixture.InitializeAsync(cancellationToken).ConfigureAwait(true);
        string package = Path.GetFullPath(Environment.GetEnvironmentVariable("CLUTCH_TEST_PACKAGE")!);
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        InstallationSettings settings = fixture.Settings with { PackageRoot = package, WebClient = new(true, port), Appearance = new("Fixture title", "Fixture subtitle") };
        InstallationRecord owner = new(Guid.NewGuid(), settings, "fixture", "unused-core", "unused-updater", "unused-client", DateTimeOffset.UtcNow);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));

        // GUIDが一致する試験専用タスクとプロファイルだけを作成・削除します。
        WebClientInstallation client = (await new WebClientInstaller(fixture.Platform).InstallAsync(owner, timeout.Token).ConfigureAwait(true))!;
        Assert.Equal(port, client.Port);
        Assert.Equal(fixture.Platform.ClientRoot, client.Root);
        Assert.False(Directory.Exists(settings.Root));
        try
        {
            if (!automaticStartup)
            {
                await RealClientProbe.CheckAsync(client, Path.Combine(fixture.Root, "client-state"), timeout.Token).ConfigureAwait(true);
            }
            else
            {
                await WebClientStartup.RegisterAsync(client, timeout.Token).ConfigureAwait(true);
                using HttpClient http = new() { BaseAddress = ClientLaunchFiles.GetUrl(port) };
                ClientBranding branding = (await http.GetFromJsonAsync<ClientBranding>(new Uri("client/branding", UriKind.Relative), ContractJson.Options, timeout.Token).ConfigureAwait(true))!;
                Assert.Equal("Fixture title", branding.ContentsName);
                Assert.Equal("Fixture subtitle", branding.Subtitle);
                Contracts.Management.Discovery.ClientNetworkState network = (await http.GetFromJsonAsync<Contracts.Management.Discovery.ClientNetworkState>(new Uri("client/network", UriKind.Relative), ContractJson.Options, timeout.Token).ConfigureAwait(true))!;
                Assert.Equal(settings.Network, network.Selected);
                ClientUpdateHealth before = (await http.GetFromJsonAsync<ClientUpdateHealth>(new Uri("client/health", UriKind.Relative), ContractJson.Options, timeout.Token).ConfigureAwait(true))!;
                await WebClientStartup.RegisterAsync(client, timeout.Token).ConfigureAwait(true);
                ClientUpdateHealth after = (await http.GetFromJsonAsync<ClientUpdateHealth>(new Uri("client/health", UriKind.Relative), ContractJson.Options, timeout.Token).ConfigureAwait(true))!;
                Assert.Equal(before.Process, after.Process);
            }
            List<OperationProgress> progress = [];
            SetupResult removed = await InstallerOperations.RemoveClientAsync(new(Guid.NewGuid(), client, false), Path.Combine(package, "updater"), progress.Add, timeout.Token).ConfigureAwait(true);
            Assert.True(removed.Outcome == EOutcome.Succeeded, removed.Detail);
            Assert.Equal(progress[^1].TotalSteps, progress[^1].CompletedSteps);
            Assert.False(File.Exists(Path.Combine(WebClientCatalog.ProgramRoot(client), client.EntryPoint)));
            Assert.False(Directory.Exists(WebClientStartup.StateDirectory(client)));
            Assert.False(Directory.Exists(settings.Root));
        }
        finally
        {
            await WebClientStartup.StopAsync(client, CancellationToken.None).ConfigureAwait(true);
            await WebClientStartup.UnregisterAsync(client, CancellationToken.None).ConfigureAwait(true);
            string state = WebClientStartup.StateDirectory(client);
            await InstallationFiles.DeleteDirectoryAsync(Path.GetDirectoryName(state)!, client.InstallationId.ToString("N"), CancellationToken.None).ConfigureAwait(true);
        }
    }
}
