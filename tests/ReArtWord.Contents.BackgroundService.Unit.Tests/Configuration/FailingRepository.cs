using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Configuration;

/// <summary>指定用途の保存失敗だけを注入します。</summary>
internal sealed class FailingRepository(IDurableRepository inner) : IDurableRepository
{
    /// <summary>保存失敗の設定にかかわらず、既存予約を変更せず照会します。</summary>
    /// <returns>実保存先の予約状態です。</returns>
    public ValueTask<bool> HasPendingWriteAsync(string scope, string key, CancellationToken cancellationToken = default) => inner.HasPendingWriteAsync(scope, key, cancellationToken);

    /// <summary>失敗させる用途です。</summary>
    public string? FailScope
    {
        get; set;
    }

    /// <summary>試験対象の読取りだけに観測や障害を追加します。</summary>
    internal Action<string, string>? Reading
    {
        get; set;
    }

    /// <summary>対象値を読みます。</summary>
    /// <returns>現在値。</returns>
    public ValueTask<DurableEntry?> ReadAsync(string scope, string key, CancellationToken cancellationToken = default)
    {
        Reading?.Invoke(scope, key);
        return inner.ReadAsync(scope, key, cancellationToken);
    }

    /// <summary>用途を列挙します。</summary>
    /// <returns>項目一覧。</returns>
    public ValueTask<IReadOnlyList<DurableEntry>> ListAsync(string scope, CancellationToken cancellationToken = default) => inner.ListAsync(scope, cancellationToken);

    /// <summary>原子保存します。</summary>
    /// <returns>保存結果。</returns>
    public ValueTask<DurableReceipt> ApplyBatchAsync(Guid id, IReadOnlyList<DurableMutation> mutations, CancellationToken cancellationToken = default) => inner.ApplyBatchAsync(id, mutations, cancellationToken);

    /// <summary>操作結果を読みます。</summary>
    /// <returns>既知結果。</returns>
    public ValueTask<DurableReceipt?> ReadReceiptAsync(Guid id, CancellationToken cancellationToken = default) => inner.ReadReceiptAsync(id, cancellationToken);

    /// <summary>指定用途だけを明示失敗にします。</summary>
    /// <returns>予約結果。</returns>
    public ValueTask<DurableReceipt> PrepareAsync(Guid id, IReadOnlyList<DurableMutation> mutations, CancellationToken cancellationToken = default)
    {
        if (mutations.Any(mutation => string.Equals(mutation.Scope, FailScope, StringComparison.Ordinal)))
        {
            return ValueTask.FromResult(new DurableReceipt(id, string.Empty, EOutcome.Failed, [], "test.diskUnavailable"));
        }
        return inner.PrepareAsync(id, mutations, cancellationToken);
    }

    /// <summary>予約を確定します。</summary>
    /// <returns>確定結果。</returns>
    public ValueTask<DurableReceipt> CommitAsync(Guid id, CancellationToken cancellationToken = default) => inner.CommitAsync(id, cancellationToken);

    /// <summary>共有値を取得します。</summary>
    /// <returns>実状態。</returns>
    public ValueTask<RepositorySnapshot> ExportAsync(CancellationToken cancellationToken = default) => inner.ExportAsync(cancellationToken);

    /// <summary>履歴本文を含まない現在状態を取得します。</summary>
    /// <returns>確認した現在状態です。</returns>
    public ValueTask<RepositorySnapshot> ReadCurrentStateAsync(CancellationToken cancellationToken = default) => inner.ReadCurrentStateAsync(cancellationToken);

    /// <summary>共有値を統合します。</summary>
    /// <returns>競合有無。</returns>
    public ValueTask<bool> ImportAsync(RepositorySnapshot snapshot, CancellationToken cancellationToken = default) => inner.ImportAsync(snapshot, cancellationToken);

    /// <summary>予約を中止します。</summary>
    /// <returns>中止結果。</returns>
    public ValueTask<DurableReceipt> AbortPreparedAsync(Guid id, CancellationToken cancellationToken = default) => inner.AbortPreparedAsync(id, cancellationToken);

    /// <summary>内容版を取得します。</summary>
    /// <returns>共有版。</returns>
    public ValueTask<string> GetSharedVersionAsync(CancellationToken cancellationToken = default) => inner.GetSharedVersionAsync(cancellationToken);

    /// <summary>ローカル診断の現在値を保存します。</summary>
    /// <returns>保存結果。</returns>
    public ValueTask<EOutcome> WriteLocalSnapshotAsync(string scope, string key, NodeValue value, CancellationToken cancellationToken = default)
    {
        if (string.Equals(scope, FailScope, StringComparison.Ordinal))
        {
            return ValueTask.FromResult(EOutcome.Failed);
        }
        return inner.WriteLocalSnapshotAsync(scope, key, value, cancellationToken);
    }

    /// <summary>競合候補を取得します。</summary>
    /// <returns>候補一覧。</returns>
    public ValueTask<IReadOnlyList<DurableEntry>> GetConflictsAsync(string scope, CancellationToken cancellationToken = default) => inner.GetConflictsAsync(scope, cancellationToken);

    /// <summary>人の明示選択を適用します。</summary>
    /// <returns>操作結果。</returns>
    public ValueTask<DurableReceipt> ResolveConflictAsync(Guid id, string scope, string key, IReadOnlyList<string> versions, NodeValue? value, CancellationToken cancellationToken = default) => inner.ResolveConflictAsync(id, scope, key, versions, value, cancellationToken);
}
