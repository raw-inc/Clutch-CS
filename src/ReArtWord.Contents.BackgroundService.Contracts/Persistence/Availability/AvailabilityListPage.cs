namespace ReArtWord.Contents.BackgroundService.Contracts.Persistence.Availability;

/// <summary>用途方針と値を含まない保存状態の件数を制限したページです。</summary>
/// <param name="Options">このPeerの事前設定です。</param>
/// <param name="Entries">現在観測できた保存・有効・復旧の区別です。</param>
/// <param name="Cursor">次ページがある場合の続き位置です。</param>
public sealed record AvailabilityListPage(AvailabilityOptions Options, IReadOnlyList<AvailabilityState> Entries, string? Cursor);
