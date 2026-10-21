namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Firewall;

/// <summary>プロファイルとページ化したルール一覧です。</summary>
/// <param name="Profiles">プロファイル。</param>
/// <param name="Rules">このページのルール。</param>
/// <param name="NextOffset">次ページ。nullは末尾。</param>
public sealed record DeviceFirewallState(IReadOnlyList<DeviceFirewallProfile> Profiles, IReadOnlyList<DeviceFirewallRule> Rules, int? NextOffset);
