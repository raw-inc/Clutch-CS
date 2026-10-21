using ReArtWord.Contents.BackgroundService.Contracts.Configuration;

namespace ReArtWord.Contents.BackgroundService.Contracts.Cluster.Conflicts;

/// <summary>候補の確定共有と、ローカル適用を区別します。</summary>
/// <param name="OperationId">元操作です。</param>
/// <param name="Saved">保存の確認です。</param>
/// <param name="Shared">固定参加者への共有確認です。</param>
/// <param name="Activation">ローカル採用の確認です。</param>
/// <param name="Issues">開始前に修正する問題です。</param>
/// <param name="Snapshot">保存候補を解消した後の自動退避です。</param>
public sealed record ConflictResolveResult(Guid OperationId, ConfigurationPhaseResult Saved, ConfigurationPhaseResult Shared,
    ConfigurationPhaseResult Activation, IReadOnlyList<ValidationIssue> Issues, ConfigurationPhaseResult? Snapshot = null);
