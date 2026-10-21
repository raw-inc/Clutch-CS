using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Contracts.Cluster.Conflicts;

/// <summary>観測した全候補に対する一回の明示解消です。</summary>
/// <param name="OperationId">再実施しない操作識別子です。</param>
/// <param name="ConflictId">対象競合です。</param>
/// <param name="ExpectedVersions">人が確認した候補集合です。</param>
/// <param name="SelectedVersion">採用する既存候補版です。</param>
/// <param name="MergedValue">比較編集した候補です。</param>
/// <param name="UseMergedValue">明示nullを含む編集値を使う意思です。</param>
public sealed record ConflictResolveRequest(Guid OperationId, string ConflictId, IReadOnlyList<string> ExpectedVersions,
    string? SelectedVersion = null, NodeValue? MergedValue = null, bool UseMergedValue = false);
