using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;

/// <summary>本文削除を有効にした保存先だけが持つ、形式を更新した履歴管理です。</summary>
internal sealed record RepositoryHistory
{
    /// <summary>時刻を持たない旧操作だけの最小索引です。</summary>
    public Dictionary<Guid, RetiredOperation> RetiredOperations
    {
        get; init;
    } = [];

    /// <summary>圧縮済み本文です。通常値の参照とは分離します。</summary>
    public Dictionary<string, JsonHistoryRecord> Archives
    {
        get; init;
    } = new(StringComparer.Ordinal);

    /// <summary>消費済みの旧受付キーです。</summary>
    public HashSet<string> RetiredKeys
    {
        get; init;
    } = new(StringComparer.Ordinal);

    /// <summary>短縮を遡及しない期限の記録です。</summary>
    public Dictionary<string, HistoryDeadline> Deadlines
    {
        get; init;
    } = new(StringComparer.Ordinal);

    /// <summary>本文が保持される確定イベントの位置です。</summary>
    public Dictionary<Guid, HistoryEventPosition> Events
    {
        get; init;
    } = [];

    /// <summary>本文削除で巻き戻らない確定位置です。</summary>
    public long Sequence
    {
        get; set;
    }

    /// <summary>現在状態だけを取得した基準点です。</summary>
    public List<JsonHistoryCheckpoint> Checkpoints
    {
        get; init;
    } = [];
}
