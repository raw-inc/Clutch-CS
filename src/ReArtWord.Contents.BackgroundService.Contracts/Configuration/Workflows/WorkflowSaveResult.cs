using System.Text.Json.Serialization;

namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;

/// <summary>各段階の結果を一つの成否に潰さない保存結果です。</summary>
/// <param name="OperationId">操作ID。</param>
/// <param name="Revision">保存候補の版。</param>
/// <param name="Validation">検証指摘。</param>
/// <param name="Saved">永続保存。</param>
/// <param name="Shared">全固定対象への共有。</param>
/// <param name="Snapshot">保存時自動スナップショット。</param>
/// <param name="Activation">このPeerでの実有効化。</param>
/// <param name="LocalOnly">離脱側のローカル保存で、クラスタ共有を行っていないか。</param>
/// <param name="BeforeSnapshot">ノード設定変更前の保護結果。nullは取得不要です。</param>
/// <param name="BeforeSnapshotId">変更前スナップショットの識別子。成功を保証する値ではありません。</param>
public sealed record WorkflowSaveResult(Guid OperationId, string? Revision, IReadOnlyList<ValidationIssue> Validation, ConfigurationPhaseResult Saved, ConfigurationPhaseResult Shared, ConfigurationPhaseResult Snapshot, ConfigurationPhaseResult Activation,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool LocalOnly = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ConfigurationPhaseResult? BeforeSnapshot = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BeforeSnapshotId = null);
