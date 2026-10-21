namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Network;

/// <summary>一つのアドレスファミリーの実設定です。</summary>
/// <param name="Enabled">プロトコルの有効状態。</param>
/// <param name="Dhcp">自動アドレス設定。未確認はnull。</param>
/// <param name="AutomaticMetric">自動メトリック。未確認はnull。</param>
/// <param name="Metric">現在のメトリック。</param>
/// <param name="AutomaticDns">DNS自動取得。未確認はnull。</param>
/// <param name="Addresses">観測したアドレス。</param>
/// <param name="Gateways">観測したデフォルトゲートウェイ。</param>
/// <param name="Dns">DNSサーバーの順序。</param>
/// <param name="RouterDiscovery">IPv6のルーター広告による自動構成。未確認またはIPv4はnull。</param>
public sealed record DeviceIpConfiguration(bool Enabled, bool? Dhcp, bool? AutomaticMetric, int? Metric, bool? AutomaticDns, IReadOnlyList<DeviceIpAddress> Addresses, IReadOnlyList<string> Gateways, IReadOnlyList<string> Dns, bool? RouterDiscovery = null);
