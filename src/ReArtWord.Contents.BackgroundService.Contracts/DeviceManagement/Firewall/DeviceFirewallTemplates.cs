namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Firewall;

/// <summary>指定された固定IDのルールだけを一括照会した結果です。</summary>
/// <param name="Rules">見つかったルールと複製可否。</param>
public sealed record DeviceFirewallTemplates(IReadOnlyList<DeviceFirewallTemplate> Rules);
