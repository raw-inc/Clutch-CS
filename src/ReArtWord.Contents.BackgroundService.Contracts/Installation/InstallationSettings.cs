using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using System.Text.Json.Serialization;

namespace ReArtWord.Contents.BackgroundService.Contracts.Installation;

/// <summary>確認画面で固定する一台の初回導入設定です。</summary>
public sealed record InstallationSettings(string Root, string PackageRoot, string ClusterId, string HostId, string PeerId,
    int Priority, bool NewCluster, bool PerUser, ClientNetworkBinding Network, ClientAppearance Appearance, bool LicenseAccepted,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WebClientSettings? WebClient = null);
