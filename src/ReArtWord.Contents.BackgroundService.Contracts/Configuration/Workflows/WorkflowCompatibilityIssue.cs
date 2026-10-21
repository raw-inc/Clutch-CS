namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;

/// <summary>互換性の確認で修正または確認が必要な範囲を示します。</summary>
/// <param name="Area">保存版、有効版、またはスナップショット内の範囲。</param>
/// <param name="SnapshotId">該当するスナップショット。通常設定ならnull。</param>
/// <param name="NodeId">該当ノード。状態全体の問題ならnull。</param>
/// <param name="NodeType">該当ノードの種類。</param>
/// <param name="ExpectedVersion">設定が期待する動作契約。</param>
/// <param name="ImplementationVersion">現在の実装の動作契約。</param>
/// <param name="SettingNames">確認対象の設定名。値や秘密は含めません。</param>
/// <param name="Issue">設定項目や接続と理由を含む指摘。</param>
public sealed record WorkflowCompatibilityIssue(string Area, string? SnapshotId, string? NodeId, string? NodeType, int? ExpectedVersion,
    int? ImplementationVersion, IReadOnlyList<string> SettingNames, ValidationIssue Issue);
