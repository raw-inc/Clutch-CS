namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Network;

/// <summary>指定した項目だけを変更するNIC設定です。</summary>
/// <param name="Family">4または6。</param>
/// <param name="Enabled">プロトコルを変更する場合だけ指定。</param>
/// <param name="Dhcp">アドレス取得方式を変更する場合だけ指定。</param>
/// <param name="AutomaticMetric">メトリック方式の変更。</param>
/// <param name="Metric">手動メトリック。</param>
/// <param name="Addresses">固定アドレスの置換。nullは変更しない。</param>
/// <param name="AutomaticDns">DNS自動取得の変更。</param>
/// <param name="Dns">DNSの置換。nullは変更しない。</param>
/// <param name="Gateways">ゲートウェイの置換。nullは変更しない。</param>
/// <param name="RouterDiscovery">IPv6のルーター広告による自動構成の明示変更。</param>
public sealed record DeviceNetworkChange(int Family, bool? Enabled = null, bool? Dhcp = null, bool? AutomaticMetric = null, int? Metric = null, IReadOnlyList<DeviceIpAddress>? Addresses = null, bool? AutomaticDns = null, IReadOnlyList<string>? Dns = null, IReadOnlyList<string>? Gateways = null, bool? RouterDiscovery = null);
