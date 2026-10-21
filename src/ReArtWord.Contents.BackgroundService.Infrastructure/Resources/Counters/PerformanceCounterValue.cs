namespace ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Counters;

/// <summary>PDHが取得した一つのカウンター実体の値です。</summary>
internal sealed record PerformanceCounterValue(string Instance, double? Value);
