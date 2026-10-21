namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Firewall;

/// <summary>プロファイルまたはルールの変更値です。</summary>
/// <param name="Enabled">有効無効の変更。</param>
/// <param name="Rule">追加するルールの基本条件。</param>
public sealed record DeviceFirewallChange(bool? Enabled = null, DeviceFirewallRule? Rule = null);
