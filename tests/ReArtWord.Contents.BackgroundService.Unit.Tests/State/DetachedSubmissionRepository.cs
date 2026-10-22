using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.State;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.State;

/// <summary>ローカルの送信意図を保存した直後に、一度だけ状態変化を挿入します。</summary>
internal sealed class DetachedSubmissionRepository : DurableRepository
{
    private readonly Lock gate = new();
    private readonly RepositoryDocument document = new();

    internal Func<CancellationToken, Task>? AfterSubmission
    {
        get; set;
    }

    /// <summary>保存の原子性を保ち、共有要求より前の境界を固定します。</summary>
    /// <returns>元の保存結果です。</returns>
    public override async ValueTask<DurableReceipt> ApplyBatchAsync(Guid operationId, IReadOnlyList<DurableMutation> mutations, CancellationToken cancellationToken = default)
    {
        DurableReceipt result = await base.ApplyBatchAsync(operationId, mutations, cancellationToken).ConfigureAwait(false);
        string scope = "peer:a:detached-state";
        if (AfterSubmission is { } next && mutations.Any(mutation => string.Equals(mutation.Scope, scope, StringComparison.Ordinal)
            && mutation.Value?.Json.Deserialize<DetachedStateEntry>()?.Submitted == true))
        {
            AfterSubmission = null;
            await next(cancellationToken).ConfigureAwait(false);
        }
        return result;
    }

    /// <summary>試験のメモリー上で、製品と同じ比較更新を直列化します。</summary>
    /// <returns>保存処理の結果です。</returns>
    private protected override T Access<T>(Func<RepositoryDocument, T> operation, bool write)
    {
        using (gate.EnterScope())
        {
            return operation(document);
        }
    }
}
