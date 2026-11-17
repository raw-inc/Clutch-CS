using ReArtWord.Contents.BackgroundService.Contracts.Resources;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Resources;

/// <summary>標本の採取時刻と応答時刻を同じPCの時計で比較できるよう、最新の標本に応答時刻を添えます。</summary>
internal sealed record ResourceSelectionSnapshot(ResourceSample? Sample, DateTimeOffset ObservedAt);
