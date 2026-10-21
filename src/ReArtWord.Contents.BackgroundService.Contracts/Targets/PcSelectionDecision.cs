namespace ReArtWord.Contents.BackgroundService.Contracts.Targets;

/// <summary>一件の実行計画で固定した候補と、リソース比較の根拠です。</summary>
/// <param name="EvaluatedAt">選択を行った時刻。</param>
/// <param name="CandidateHostIds">複数の選択を合わせ、重複を除いたPC。</param>
/// <param name="Observations">比較に使用した実測値と欠測理由。</param>
/// <param name="CandidateProblems">未発見・不在など、各候補PCの選択時点の問題。</param>
public sealed record PcSelectionDecision(DateTimeOffset EvaluatedAt, IReadOnlyList<string> CandidateHostIds,
    IReadOnlyList<PcSelectionObservation> Observations,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, string>? CandidateProblems = null);
