namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;

/// <summary>一つのPeer上で確認した互換性の問題と、未取得範囲を返します。</summary>
/// <param name="WorkflowId">確認したワークフロー。</param>
/// <param name="PeerId">比較に使用した実装のPeer。</param>
/// <param name="SavedRevision">確認した保存版。</param>
/// <param name="EffectiveRevision">確認した有効版。</param>
/// <param name="Issues">確認対象と理由。</param>
/// <param name="IssuesOmitted">応答上限のため指摘を一部省略したか。</param>
/// <param name="InspectedSnapshots">今回確認した、このPeerのスナップショット数。</param>
/// <param name="NextSnapshot">続きがあれば次の照会へ渡す位置。</param>
public sealed record WorkflowCompatibilityReport(string WorkflowId, string PeerId, string? SavedRevision, string? EffectiveRevision,
    IReadOnlyList<WorkflowCompatibilityIssue> Issues, bool IssuesOmitted, int InspectedSnapshots, string? NextSnapshot);
