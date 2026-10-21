namespace ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;

/// <summary>一つのレベルの非圧縮期間と総保持期間です。TotalDaysのnullはzstd無期限です。</summary>
/// <param name="Level">Levelの指定値。</param>
/// <param name="RawDays">RawDaysの指定値。</param>
/// <param name="TotalDays">TotalDaysの指定値。</param>
public sealed record LogRetentionRule(string Level, int RawDays, int? TotalDays);
