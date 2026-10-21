namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;

/// <summary>共通設定の保存・共有・スナップショット・ローカル実適用を分離します。</summary>
/// <param name="OperationId">保存操作です。</param>
/// <param name="Revision">候補版です。</param>
/// <param name="Validation">設定の問題です。</param>
/// <param name="Saved">ローカル永続保存結果です。</param>
/// <param name="Shared">固定参加集合への共有結果です。</param>
/// <param name="Snapshot">保存時スナップショット結果です。</param>
/// <param name="Activation">ローカル実適用結果です。</param>
public sealed record ClusterOperationalSaveResult(Guid OperationId, string? Revision, IReadOnlyList<ValidationIssue> Validation, ConfigurationPhaseResult Saved, ConfigurationPhaseResult Shared, ConfigurationPhaseResult Snapshot, ConfigurationPhaseResult Activation);
