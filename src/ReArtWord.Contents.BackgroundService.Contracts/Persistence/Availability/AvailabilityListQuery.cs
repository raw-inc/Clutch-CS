namespace ReArtWord.Contents.BackgroundService.Contracts.Persistence.Availability;

/// <summary>保存状態の件数を制限したページを指定します。</summary>
/// <param name="Limit">一回に取得する最大件数です。</param>
/// <param name="Cursor">同じ状態集合の続きだけを指す位置です。</param>
public sealed record AvailabilityListQuery(int Limit = 50, string? Cursor = null);
