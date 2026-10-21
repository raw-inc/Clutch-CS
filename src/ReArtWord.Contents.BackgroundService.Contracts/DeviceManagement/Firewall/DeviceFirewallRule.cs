namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Firewall;

/// <summary>一つのFirewallルールと基本的なフィルターです。</summary>
/// <param name="Id">正確に一致させるルール名。</param>
/// <param name="Name">表示名。</param>
/// <param name="Direction">InboundまたはOutbound。</param>
/// <param name="Action">AllowまたはBlock。</param>
/// <param name="Enabled">有効状態。</param>
/// <param name="Profile">適用プロファイル。</param>
/// <param name="Protocol">プロトコル。</param>
/// <param name="LocalPorts">ローカルポート。</param>
/// <param name="RemotePorts">リモートポート。</param>
/// <param name="LocalAddresses">ローカルアドレス。</param>
/// <param name="RemoteAddresses">リモートアドレス。</param>
/// <param name="Program">プログラムのフィルター。</param>
/// <param name="Editable">ローカルで編集できるか。</param>
/// <param name="Revision">このルールの内容版。</param>
/// <param name="DetailsHash">追加のOS条件も変更検出に含める内容識別子。</param>
public sealed record DeviceFirewallRule(string Id, string Name, string Direction, string Action, bool Enabled, string Profile, string Protocol, IReadOnlyList<string> LocalPorts, IReadOnlyList<string> RemotePorts, IReadOnlyList<string> LocalAddresses, IReadOnlyList<string> RemoteAddresses, string Program, bool Editable, string? Revision = null, string? DetailsHash = null);
