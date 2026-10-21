namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Firewall;

/// <summary>Windows Firewallプロファイルの実状態です。</summary>
/// <param name="Name">Domain・Private・Public。</param>
/// <param name="Enabled">実効的な有効状態。</param>
/// <param name="Revision">このプロファイルの内容版。</param>
public sealed record DeviceFirewallProfile(string Name, bool Enabled, string? Revision = null);
