using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Installation;

/// <summary>プログラム更新から独立した管理画面の設定を用意します。</summary>
public static class WebClientProfile
{
    private static string ServiceFileName => "client-service.json";

    /// <summary>利用者が変更した表示設定を保持し、未作成の場合だけ初期値を保存します。</summary>
    /// <returns>表示設定とサービス設定の準備です。</returns>
    public static async Task InitializeAsync(WebClientInstallation client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        string state = WebClientStartup.StateDirectory(client);
        new InstallationPlatform().PrepareDirectory(state, !client.RunAsService, privateData: true);
        string branding = Path.Combine(state, "branding.json");
        if (!File.Exists(branding))
        {
            string appearancePath = Path.Combine(client.Root, "config", "appearance.json");
            ClientAppearance appearance = new();
            if (File.Exists(appearancePath))
            {
                appearance = JsonSerializer.Deserialize<ClientAppearance>(await File.ReadAllTextAsync(appearancePath, cancellationToken).ConfigureAwait(false), ContractJson.Options) ?? new();
            }
            ClientBranding value = new(appearance.Title ?? ClientAppearance.DefaultTitle, HeaderLogo: appearance.IconPath, Favicon: appearance.FaviconPath,
                Subtitle: appearance.Subtitle ?? ClientAppearance.DefaultSubtitle);
            await InstallationFiles.WriteAsync(branding, value, cancellationToken).ConfigureAwait(false);
        }
        if (client.RunAsService)
        {
            await InstallationFiles.WriteAsync(Path.Combine(state, ServiceFileName), new ClientServiceSettings(WebClientStartup.ServiceName(client)), cancellationToken).ConfigureAwait(false);
        }
        string networks = Path.Combine(state, "networks.json");
        string legacyNetwork = Path.Combine(state, "network.json");
        string initialNetwork = Path.Combine(client.Root, "config", "network.json");
        if (!File.Exists(networks) && !File.Exists(legacyNetwork) && File.Exists(initialNetwork))
        {
            ClientNetworkBinding binding = JsonSerializer.Deserialize<ClientNetworkBinding>(await File.ReadAllTextAsync(initialNetwork, cancellationToken).ConfigureAwait(false), ContractJson.Options)
                ?? throw new InvalidDataException("The initial client network is missing.");
            await InstallationFiles.WriteAsync(networks, new[] { binding }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>サービス導入の場合だけ明示した登録名を返します。</summary>
    /// <returns>登録名、通常のユーザー起動ならnullです。</returns>
    public static string? ReadServiceName(string stateDirectory)
    {
        string path = Path.Combine(stateDirectory, ServiceFileName);
        if (!File.Exists(path))
        {
            return null;
        }
        ClientServiceSettings settings = JsonSerializer.Deserialize<ClientServiceSettings>(File.ReadAllText(path), ContractJson.Options)
            ?? throw new InvalidDataException("The client service settings are missing.");
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.Name);
        return settings.Name;
    }
}
