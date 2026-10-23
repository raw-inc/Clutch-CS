using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;

/// <summary>共通設定とワークフローの保存版・実有効版をまとめた持出し形式です。</summary>
/// <param name="FormatVersion">アーカイブ形式の版です。</param>
/// <param name="CapturedAt">設定を固定した時刻です。</param>
/// <param name="ClusterId">取得元クラスターです。</param>
/// <param name="PeerId">取得元のピアです。</param>
/// <param name="Operational">共通運用設定の保存版と有効版です。</param>
/// <param name="Workflows">草稿と有効状態を区別したワークフローです。</param>
/// <param name="IncludesLogs">保持中のログ本文・履歴を含むかです。</param>
/// <param name="LogPeers">ログを取得した固定ピア集合です。</param>
public sealed record ClusterArchiveConfiguration(int FormatVersion, DateTimeOffset CapturedAt, string ClusterId, string PeerId,
    ClusterOperationalState Operational, IReadOnlyList<ArchivedWorkflow> Workflows, bool IncludesLogs, IReadOnlyList<string>? LogPeers = null);
