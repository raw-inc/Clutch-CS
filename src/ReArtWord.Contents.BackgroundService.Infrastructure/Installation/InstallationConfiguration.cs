using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Loading;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Installation;

/// <summary>GUIの選択を既存CoreのTOML設定へ変換します。</summary>
public static class InstallationConfiguration
{
    /// <summary>更新領域とDBをサービスの入替領域から分離します。</summary>
    /// <returns>既存readerで検証したTOMLです。</returns>
    public static string Create(InstallationRecord record, bool resynchronize = false)
    {
        ArgumentNullException.ThrowIfNull(record);
        InstallationSettings settings = record.Settings;
        string kind = "DirectProcess";
        if (!settings.PerUser)
        {
            kind = "Systemd";
            if (OperatingSystem.IsWindows())
            {
                kind = "WindowsService";
            }
            else
            {
                if (OperatingSystem.IsMacOS())
                {
                    kind = "Launchd";
                }
            }
        }
        string initialize = "false";
        if (settings.NewCluster && !resynchronize)
        {
            initialize = "true";
        }
        string text = $"""
            workflows = []
            [cluster]
            id = {Quote(settings.ClusterId)}
            peerId = {Quote(settings.PeerId)}
            hostId = {Quote(settings.HostId)}
            priority = {settings.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture)}
            initialize = {initialize}
            address = {Quote(settings.Network.BindAddress)}
            broadcast = {Quote(settings.Network.BroadcastAddress)}
            port = {settings.Network.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)}
            interfaceId = {Quote(settings.Network.InterfaceId)}
            [storage]
            directory = {Quote(Path.Combine(settings.Root, "data"))}
            backend = "sqlite"
            [files]
            roots = [{Quote(Path.Combine(settings.Root, "workspace"))}]
            [logs]
            directory = {Quote(Path.Combine(settings.Root, "logs"))}
            interfaceId = {Quote(settings.Network.InterfaceId)}
            [updates]
            installationRoot = {Quote(Path.Combine(settings.Root, "core"))}
            stagingRoot = {Quote(Path.Combine(settings.Root, "staging"))}
            backupRoot = {Quote(Path.Combine(settings.Root, "backups"))}
            updaterExecutablePath = {Quote(Path.Combine(settings.Root, "updater", record.UpdaterEntryPoint))}
            serviceKind = {Quote(kind)}
            serviceName = {Quote(record.ServiceName)}
            interfaceId = {Quote(settings.Network.InterfaceId)}
            """;
        _ = CommonConfigurationReader.Read(text, settings.Root);
        return text;
    }

    /// <summary>TOML basic stringで有効なescapeを使用します。</summary>
    /// <returns>引用済み文字列です。</returns>
    private static string Quote(string value) => JsonSerializer.Serialize(value);
}
