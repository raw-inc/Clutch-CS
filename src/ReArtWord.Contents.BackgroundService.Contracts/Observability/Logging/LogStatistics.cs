namespace ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;

/// <summary>ログ障害を本来処理から分離して表示する統計です。</summary>
/// <param name="QueuedBytes">QueuedBytesの指定値。</param>
/// <param name="DroppedEntries">DroppedEntriesの指定値。</param>
/// <param name="DroppedBytes">DroppedBytesの指定値。</param>
/// <param name="PersistedEntries">PersistedEntriesの指定値。</param>
/// <param name="ForwardPendingBytes">ForwardPendingBytesの指定値。</param>
/// <param name="SearchAvailable">SearchAvailableの指定値。</param>
/// <param name="ListenEndpoint">ListenEndpointの指定値。</param>
/// <param name="LastFailureCode">LastFailureCodeの指定値。</param>
/// <param name="Failures">現在回復していない段階別の原因種類。</param>
public sealed record LogStatistics(long QueuedBytes, long DroppedEntries, long DroppedBytes, long PersistedEntries, long ForwardPendingBytes, bool SearchAvailable, string? ListenEndpoint, string? LastFailureCode = null, IReadOnlyDictionary<string, string>? Failures = null);
