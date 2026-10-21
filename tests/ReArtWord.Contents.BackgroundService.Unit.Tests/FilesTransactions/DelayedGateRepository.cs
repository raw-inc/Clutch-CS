using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.FilesTransactions;

/// <summary>保存規則を維持し、ファイル予約の準備だけを通信期限より長く保留します。</summary>
internal sealed class DelayedGateRepository : DurableRepository
{
    private readonly Lock gate = new();
    private readonly RepositoryDocument document = new();
    private bool paused;

    internal bool Reject
    {
        get; init;
    }

    internal int Preparations
    {
        get; private set;
    }

    internal TaskCompletionSource Entered
    {
        get; private set;
    } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource Resume
    {
        get; private set;
    } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>次の一回のファイル保存だけを保留します。</summary>
    internal void Pause()
    {
        Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        paused = true;
    }

    /// <summary>生存通知や結果照会を止めずに予約処理の遅延を再現します。</summary>
    /// <returns>通常の準備結果、または確定した試験用の拒否です。</returns>
    public override async ValueTask<DurableReceipt> PrepareAsync(Guid operationId, IReadOnlyList<DurableMutation> mutations, CancellationToken cancellationToken = default)
    {
        string reservations = "file:reservations";
        if (mutations.Any(mutation => string.Equals(mutation.Scope, reservations, StringComparison.Ordinal)))
        {
            Preparations++;
            if (paused)
            {
                paused = false;
                Entered.TrySetResult();
                await Resume.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            if (Reject)
            {
                return new(operationId, string.Empty, EOutcome.Failed, [], "fixture.rejected");
            }
        }
        return await base.PrepareAsync(operationId, mutations, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>製品と同じ原子的な保存規則を試験文書へ適用します。</summary>
    /// <returns>保存操作の結果です。</returns>
    private protected override T Access<T>(Func<RepositoryDocument, T> operation, bool write)
    {
        using (gate.EnterScope())
        {
            return operation(document);
        }
    }
}
