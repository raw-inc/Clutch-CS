namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

/// <summary>基準点の間の確定変更を識別します。</summary>
/// <param name="Sequence">確定位置です。</param>
/// <param name="RecordedAt">確定日時です。</param>
internal sealed record HistoryEventPosition(long Sequence, DateTimeOffset RecordedAt);
