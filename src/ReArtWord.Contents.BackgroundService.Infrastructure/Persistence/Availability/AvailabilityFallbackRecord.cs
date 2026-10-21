using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Availability;

/// <summary>明示代替媒体へ保存する一つの内部候補です。</summary>
/// <param name="Purpose">用途です。</param>
/// <param name="DatasetId">対象です。</param>
/// <param name="Revision">候補の内容版です。</param>
/// <param name="Value">候補の実値です。</param>
/// <param name="BaseRevision">代替保存を必要としたprimary確認版です。</param>
internal sealed record AvailabilityFallbackRecord(EStoragePurpose Purpose, string DatasetId, string Revision, NodeValue Value, string? BaseRevision);
