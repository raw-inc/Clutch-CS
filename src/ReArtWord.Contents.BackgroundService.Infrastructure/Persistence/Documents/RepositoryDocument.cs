using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using System.Text.Json.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;

/// <summary>一取引で保存する状態と操作結果です。</summary>
internal sealed record RepositoryDocument
{
    /// <summary>取引中だけ有効な祖先参照です。本文履歴を文書全体へ保持せず、保存形式にも含めません。</summary>
    [JsonIgnore]
    internal Func<string, string, IReadOnlyCollection<string>, IReadOnlyList<DurableEntry>>? ReadAncestors
    {
        get; init;
    }

    /// <summary>旧操作の本文を捨てた後も再実行を拒否する小さい索引です。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RepositoryHistory? History
    {
        get; set;
    }

    /// <summary>現在の項目です。</summary>
    public Dictionary<string, DurableEntry> Entries
    {
        get; init;
    } = new(StringComparer.Ordinal);

    /// <summary>過去の操作結果です。</summary>
    public Dictionary<Guid, DurableReceipt> Receipts
    {
        get; init;
    } = [];

    /// <summary>複製確定待ちの変更です。</summary>
    public Dictionary<Guid, PreparedBatch> Prepared
    {
        get; init;
    } = [];

    /// <summary>自動選択できない分岐候補を保持します。</summary>
    public List<DurableEntry> Conflicts
    {
        get; init;
    } = [];
}
