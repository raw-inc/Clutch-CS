using ReArtWord.Contents.BackgroundService.Contracts.Retention;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

/// <summary>既存行に対する削除期限を設定変更と独立して保持します。</summary>
/// <param name="Category">保持対象です。</param>
/// <param name="RecordedAt">記録日時です。</param>
/// <param name="DeleteAt">固定した削除期限、nullは永久です。</param>
internal sealed record HistoryDeadline(ERetentionCategory Category, DateTimeOffset RecordedAt, DateTimeOffset? DeleteAt);
