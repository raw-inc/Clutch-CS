using ReArtWord.Contents.BackgroundService.Contracts.Resources;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Resources;

/// <summary>採取元での経過時間を求めるため、標本と応答時刻を一緒に返します。</summary>
internal sealed record ResourceSelectionSnapshot(ResourceSample? Sample, DateTimeOffset ObservedAt);
