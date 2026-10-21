using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;

/// <summary>用途別の原子的な比較更新と操作確認を行います。</summary>
public interface IDurableRepository
{
    /// <summary>削除した履歴の受付キーを再利用できないことを確認します。</summary>
    /// <returns>元結果が消えていても新規実行を拒否するキーかです。</returns>
    public ValueTask<bool> IsHistoryRetiredAsync(string scope, string key, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);

    /// <summary>共有複製の全対象へ渡す同じ因果情報を、送信前に固定します。</summary>
    /// <returns>変更本文と同じ親版に対応する送信候補です。</returns>
    public ValueTask<IReadOnlyList<DurableMutation>> StampAsync(IReadOnlyList<DurableMutation> mutations, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(mutations);

    /// <summary>削除履歴を含め一項目を読みます。</summary>
    /// <returns>保存履歴、または未作成のnull。</returns>
    public ValueTask<DurableEntry?> ReadAsync(string scope, string key, CancellationToken cancellationToken = default);

    /// <summary>保存準備中の操作が指定項目を予約しているか、変更せず確認します。</summary>
    /// <returns>予約が残る場合はtrueです。確認後の競合は更新時にも検査します。</returns>
    public ValueTask<bool> HasPendingWriteAsync(string scope, string key, CancellationToken cancellationToken = default);

    /// <summary>指定用途の保存履歴を列挙します。</summary>
    /// <returns>保存済み項目。</returns>
    public ValueTask<IReadOnlyList<DurableEntry>> ListAsync(string scope, CancellationToken cancellationToken = default);

    /// <summary>整合性検査を省かず、確認済み本文の再生成だけを省いて現在集合を観測します。</summary>
    /// <returns>対象集合の指紋と変更本文です。keysがnullなら用途内の全項目を対象にします。</returns>
    public ValueTask<IReadOnlyList<DurableObservation>> ObserveAsync(string scope, IReadOnlyDictionary<string, string> known,
        IReadOnlySet<string>? keys = null, CancellationToken cancellationToken = default)
        => RepositoryObservations.ReadAsync(this, scope, known, keys, cancellationToken);

    /// <summary>全項目の期待版が一致した場合だけ一括保存します。</summary>
    /// <returns>再照会可能な確定結果。</returns>
    public ValueTask<DurableReceipt> ApplyBatchAsync(Guid operationId, IReadOnlyList<DurableMutation> mutations, CancellationToken cancellationToken = default);

    /// <summary>操作の保存結果を問い合わせます。</summary>
    /// <returns>既知結果、または未受理のnull。</returns>
    public ValueTask<DurableReceipt?> ReadReceiptAsync(Guid operationId, CancellationToken cancellationToken = default);

    /// <summary>まだ確定・中止されていない元操作の準備内容を、変更せず確認します。</summary>
    /// <returns>準備済み内容の指紋、未準備・確定済み・中止済みならnullです。</returns>
    public ValueTask<string?> ReadPreparationAsync(Guid operationId, CancellationToken cancellationToken = default) => ValueTask.FromResult<string?>(null);

    /// <summary>共有変更を原子的に予約します。</summary>
    /// <returns>予約確認結果。</returns>
    public ValueTask<DurableReceipt> PrepareAsync(Guid operationId, IReadOnlyList<DurableMutation> mutations, CancellationToken cancellationToken = default);

    /// <summary>予約した変更だけを確定します。</summary>
    /// <returns>確定結果。</returns>
    public ValueTask<DurableReceipt> CommitAsync(Guid operationId, CancellationToken cancellationToken = default);

    /// <summary>ローカル情報を除く実共有内容を読みます。</summary>
    /// <returns>共有状態と変更履歴。</returns>
    public ValueTask<RepositorySnapshot> ExportAsync(CancellationToken cancellationToken = default);

    /// <summary>過去の操作結果本文を復元せず、現在値・競合・復旧境界・未確定予約を確認します。</summary>
    /// <returns>操作結果本文を含まない現在状態です。過去本文は照会・同期で必要になった時に検証します。</returns>
    public ValueTask<RepositorySnapshot> ReadCurrentStateAsync(CancellationToken cancellationToken = default);

    /// <summary>変更履歴を比較し、非競合だけを取り込みます。</summary>
    /// <returns>競合がなく全変更を確認できたか。</returns>
    public ValueTask<bool> ImportAsync(RepositorySnapshot snapshot, CancellationToken cancellationToken = default);

    /// <summary>未確定予約を中止し、後着コミットを拒否します。</summary>
    /// <returns>中止結果。確定済みなら元の確定結果。</returns>
    public ValueTask<DurableReceipt> AbortPreparedAsync(Guid operationId, CancellationToken cancellationToken = default);

    /// <summary>変更履歴全体を読まず現在共有値の版を確認します。</summary>
    /// <returns>共有状態の内容識別子。</returns>
    public ValueTask<string> GetSharedVersionAsync(CancellationToken cancellationToken = default);

    /// <summary>ローカル診断値を履歴・操作記録を増やさず原子的に置換します。</summary>
    /// <returns>保存確認結果。</returns>
    public ValueTask<ReArtWord.Contents.BackgroundService.Contracts.Execution.EOutcome> WriteLocalSnapshotAsync(string scope, string key, ReArtWord.Contents.BackgroundService.Contracts.Nodes.NodeValue value, CancellationToken cancellationToken = default);

    /// <summary>自動選択しなかった競合候補を取得します。空スコープは全用途を対象とします。</summary>
    /// <returns>現在値を含む候補一覧。</returns>
    public ValueTask<IReadOnlyList<DurableEntry>> GetConflictsAsync(string scope, CancellationToken cancellationToken = default);

    /// <summary>確認した候補集合に対して人が選んだ値を確定します。</summary>
    /// <returns>候補集合の変化も区別する操作結果。</returns>
    public ValueTask<DurableReceipt> ResolveConflictAsync(Guid operationId, string scope, string key, IReadOnlyList<string> expectedVersions, ReArtWord.Contents.BackgroundService.Contracts.Nodes.NodeValue? value, CancellationToken cancellationToken = default);
}
