using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;

/// <summary>アーカイブに保存する共通設定とワークフロー。</summary>
/// <param name="FormatVersion">アーカイブ形式の版です。</param>
/// <param name="CapturedAt">設定を取得した時刻。</param>
/// <param name="ClusterId">取得元クラスターです。</param>
/// <param name="PeerId">取得元のピアです。</param>
/// <param name="Operational">共通運用設定の保存版と有効版です。</param>
/// <param name="Workflows">各ワークフローの保存版と有効版。</param>
/// <param name="IncludesLogs">保持中のログ本文・履歴を含める場合はtrue。</param>
/// <param name="LogPeers">ログを収集したピアの一覧。</param>
public sealed record ClusterArchiveConfiguration(int FormatVersion, DateTimeOffset CapturedAt, string ClusterId, string PeerId,
    ClusterOperationalState Operational, IReadOnlyList<ArchivedWorkflow> Workflows, bool IncludesLogs, IReadOnlyList<string>? LogPeers = null);
