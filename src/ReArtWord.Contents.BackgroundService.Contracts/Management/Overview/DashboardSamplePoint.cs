namespace ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;

/// <summary>項目の説明を共有する、Dashboard配信用の一計測点です。</summary>
/// <param name="Id">元の採取識別子です。差分取得の位置を維持します。</param>
/// <param name="ObservedAt">元の採取時刻です。</param>
/// <param name="Values">テンプレートと同じ順序の実測値です。欠測はnullです。</param>
public sealed record DashboardSamplePoint(Guid Id, DateTimeOffset ObservedAt, IReadOnlyList<double?> Values);
