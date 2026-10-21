namespace ReArtWord.Contents.BackgroundService.Contracts.Execution;

/// <summary>通常入力の保留・停止保持・実行数と値の保持サイズです。heap使用量の推計ではありません。</summary>
public sealed record SourceQueueStatistics(int Waiting, int Held, int Running, long RetainedValueBytes, long MaximumValueBytes);
