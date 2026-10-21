namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Firewall;

/// <summary>一括状態編集で、画面の入力項目だけで再現可能かを確認したルールです。</summary>
/// <param name="Rule">実取得したルールと内容版。</param>
/// <param name="Cloneable">隠れた条件を失わず、別ホストへ同じ条件を作成できるか。</param>
public sealed record DeviceFirewallTemplate(DeviceFirewallRule Rule, bool Cloneable);
