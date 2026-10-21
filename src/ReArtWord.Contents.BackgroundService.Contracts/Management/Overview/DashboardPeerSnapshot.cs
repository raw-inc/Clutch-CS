using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.History;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;

namespace ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;

/// <summary>ClientHostが実体ごとに保持する有限の観測です。受信時刻は再配信時に更新しません。</summary>
/// <param name="ClusterId">対象クラスタです。</param>
/// <param name="Peer">観測した実体です。</param>
/// <param name="CheckedAt">状態の実確認時刻です。</param>
/// <param name="Status">管理状態のスナップショットです。</param>
/// <param name="Health">稼働・版の確認結果です。</param>
/// <param name="Samples">最大121点のリソース履歴です。</param>
/// <param name="Executions">有限の直近実行結果です。</param>
/// <param name="Logs">有限の直近ログです。</param>
/// <param name="Problem">今回の観測不能理由です。</param>
/// <param name="NodeExecutions">出力値を含まないノード別の最新成否です。</param>
/// <param name="SampleSeries">対応する画面が明示要求した場合だけ返す、項目説明を共有したリソース履歴です。</param>
public sealed record DashboardPeerSnapshot(string ClusterId, PeerInfo Peer, DateTimeOffset? CheckedAt,
    JsonElement? Status, JsonElement? Health, IReadOnlyList<ResourceSample> Samples,
    ExecutionHistoryPage? Executions = null, LogQueryResult? Logs = null, string? Problem = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<NodeExecutionObservation>? NodeExecutions = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<DashboardSampleSeries>? SampleSeries = null);
