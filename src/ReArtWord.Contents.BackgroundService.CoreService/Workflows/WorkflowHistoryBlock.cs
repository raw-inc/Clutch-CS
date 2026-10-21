namespace ReArtWord.Contents.BackgroundService.CoreService.Workflows;

/// <summary>同じ保持境界に属する連続した実行結果を、本文を失わず一つの行へ保存します。</summary>
/// <param name="FirstSequence">先頭結果の循環保存世代です。</param>
/// <param name="Count">連続した結果の件数です。</param>
/// <param name="CompletedAt">同じUTC秒内の最後の完了時刻です。</param>
/// <param name="Format">圧縮形式です。</param>
/// <param name="Length">展開後の正確な長さです。</param>
/// <param name="Hash">展開後のSHA-256です。</param>
/// <param name="Data">圧縮した結果配列です。</param>
internal sealed record WorkflowHistoryBlock(long FirstSequence, int Count, DateTimeOffset CompletedAt, string Format, int Length, string Hash, byte[] Data);
