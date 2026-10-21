using ReArtWord.Contents.BackgroundService.Contracts.Collaboration;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Collaboration;

/// <summary>並行編集を保存版ではなく共通の編集時点から比較します。</summary>
internal sealed record DraftCheckpoint(IReadOnlyDictionary<Guid, long> Clock, DraftView Draft);
