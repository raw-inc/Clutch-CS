using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Installer.Tests;

/// <summary>管理画面の設定をプログラム更新から分離し、利用者の変更を保持します。</summary>
public sealed class WebClientProfileTests
{
    /// <summary>初回だけ導入時の表示設定を保存し、その後の起動では上書きしません。</summary>
    /// <returns>専用プロファイルの保存と保持の検証です。</returns>
    [Fact]
    public async Task ProfileKeepsUserChangesOutsideTheUpdatedProgram()
    {
        using SetupFixture fixture = new();
        await fixture.InitializeAsync();
        string config = Path.Combine(fixture.Root, "config");
        Directory.CreateDirectory(config);
        await InstallationFiles.WriteAsync(Path.Combine(config, "appearance.json"), new ClientAppearance("Configured", "Subtitle"));
        await InstallationFiles.WriteAsync(Path.Combine(config, "network.json"), fixture.Settings.Network);
        WebClientInstallation client = new(Guid.NewGuid(), fixture.Root, "ClientHost.exe", WebClientSettings.DefaultPort, true,
            AutomaticStartup: true, ProgramDirectory: Path.Combine(fixture.Root, "app"));
        string state = WebClientStartup.StateDirectory(client);
        try
        {
            await WebClientProfile.InitializeAsync(client);
            string path = Path.Combine(state, "branding.json");
            ClientBranding initial = JsonSerializer.Deserialize<ClientBranding>(await File.ReadAllTextAsync(path), ContractJson.Options)!;
            Assert.Equal("Configured", initial.ContentsName);
            Assert.Equal("Subtitle", initial.Subtitle);
            string networks = Path.Combine(state, "networks.json");
            Assert.Equal([fixture.Settings.Network], JsonSerializer.Deserialize<ClientNetworkBinding[]>(await File.ReadAllTextAsync(networks), ContractJson.Options)!);
            Assert.Null(WebClientProfile.ReadServiceName(state));
            Assert.False(state.StartsWith(WebClientCatalog.ProgramRoot(client), StringComparison.OrdinalIgnoreCase));
            ClientBranding changed = initial with { ContentsName = "User choice" };
            await InstallationFiles.WriteAsync(path, changed);
            await InstallationFiles.WriteAsync(networks, Array.Empty<ClientNetworkBinding>());
            await WebClientProfile.InitializeAsync(client);
            Assert.Equal(changed, JsonSerializer.Deserialize<ClientBranding>(await File.ReadAllTextAsync(path), ContractJson.Options));
            Assert.Empty(JsonSerializer.Deserialize<ClientNetworkBinding[]>(await File.ReadAllTextAsync(networks), ContractJson.Options)!);
        }
        finally
        {
            InstallationFiles.DeleteDirectory(Path.GetDirectoryName(state)!, client.InstallationId.ToString("N"));
        }
    }
}
