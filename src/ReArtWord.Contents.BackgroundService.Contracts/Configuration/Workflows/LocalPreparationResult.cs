namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;

/// <summary>準備状態と、個別に利用できない定義の問題です。</summary>
public sealed record LocalPreparationResult(ELocalPreparationState State, IReadOnlyList<ValidationIssue> Issues, string? Reason = null);
