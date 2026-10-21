namespace ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;

/// <summary>一括照会する観測実体と、受信済み履歴の位置です。</summary>
/// <param name="ClusterId">観測対象のクラスタです。</param>
/// <param name="PeerId">クラスタ内のピアです。</param>
/// <param name="InstanceId">混入させない固定実体です。</param>
/// <param name="After">受信済みの最終計測点です。</param>
/// <param name="HealthOnly">一覧に必要な稼働確認だけを返します。</param>
/// <param name="DiagnosticsOnly">編集画面に必要な状態と最新ノード成否だけを返します。</param>
public sealed record DashboardSnapshotQuery(string ClusterId, string PeerId, Guid InstanceId, Guid? After = null, bool HealthOnly = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool DiagnosticsOnly = false);
