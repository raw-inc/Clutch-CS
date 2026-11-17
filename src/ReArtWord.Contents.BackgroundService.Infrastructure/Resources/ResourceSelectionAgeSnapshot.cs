using ReArtWord.Contents.BackgroundService.Contracts.Resources;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Resources;

/// <summary>日時を引き算せず、採取元が単調時計で計測した標本の古さを返します。</summary>
internal sealed record ResourceSelectionAgeSnapshot(ResourceSample? Sample, TimeSpan? Age);
