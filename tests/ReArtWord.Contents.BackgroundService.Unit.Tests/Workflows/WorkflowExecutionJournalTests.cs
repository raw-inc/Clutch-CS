using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Retention;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting;
using ReArtWord.Contents.BackgroundService.CoreService.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>背景保存した診断を復元しても業務処理を再実行せず、保存範囲を有限にします。</summary>
public sealed class WorkflowExecutionJournalTests
{
    /// <summary>結果を保存して再読込みし、同じ診断を再記録して行数を増やしません。</summary>
    /// <returns>保存済みの結果と副作用一回の確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistedResultsCanBeReadAfterRestartWithoutExecutingTheWorkflow(bool large)
    {
        using MediaTestFiles files = new();
        SqliteRepository repository = new(Path.Combine(files.Root, "state.db"));
        int effects = 0;
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode((_, _) =>
        {
            effects++;
            string value = large ? new string('x', 12000)
                                 : "42";
            return ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject(value)));
        }));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        using WorkflowExecutionJournal journal = new(repository, "a", fixture.Engine);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(fixture.Compiler.Compile(new("history", "history", [WorkflowFixture.Definition("action")], [])).Workflow!);
        await journal.FlushAsync();
        Assert.Equal(report.Id, WorkflowExecutionJournal.Identity(Assert.Single(journal.Recent())));
        using WorkflowExecutionJournal restored = new(repository, "a", fixture.Engine);
        await restored.FlushAsync();
        Assert.Null(restored.Problem);
        Assert.Equal(report.Id, WorkflowExecutionJournal.Identity(Assert.Single(restored.Recent())));
        Assert.Equal(ContractJson.Hash(Assert.Single(journal.Recent())), ContractJson.Hash(Assert.Single(restored.Recent())));
        Assert.Single(await repository.ListAsync("peer:a:workflow-results"));
        if (large)
        {
            Assert.True(Assert.Single(await repository.ListAsync("peer:a:workflow-result-details")).Value!.PayloadSize() < 12000);
        }
        Assert.Equal(1, effects);
    }

    /// <summary>原子保存が未確認ならメモリ上でも確定せず、次回だけ同じ診断を再保存します。</summary>
    /// <returns>再実行なしの回復と重複書込み防止です。</returns>
    [Fact]
    public async Task FailedBatchRetriesHistoryWithoutReplayingActionsAsync()
    {
        MemoryRepository repository = new() { Reject = true };
        int effects = 0;
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode((_, _) =>
        {
            effects++;
            return ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject(new string('x', 6000))));
        }));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        using WorkflowExecutionJournal journal = new(repository, "a", fixture.Engine);
        Runtime.Workflows.Compilation.CompiledWorkflow plan = fixture.Compiler.Compile(new("history", "history", [WorkflowFixture.Definition("action")], [])).Workflow!;
        for (int index = 0; index < 3; index++)
        {
            await fixture.Engine.ExecuteAsync(plan).ConfigureAwait(true);
        }
        await journal.FlushAsync().ConfigureAwait(true);
        Assert.NotNull(journal.Problem);
        Assert.Empty(journal.Recent());
        Assert.Empty(await repository.ListAsync("peer:a:workflow-results").ConfigureAwait(true));
        repository.Reject = false;
        await journal.FlushAsync().ConfigureAwait(true);
        Assert.Null(journal.Problem);
        Assert.Equal(3, journal.Recent().Count);
        Assert.Equal(1, repository.Writes);
        await journal.FlushAsync().ConfigureAwait(true);
        Assert.Equal(1, repository.Writes);
        Assert.Equal(3, effects);
    }

    /// <summary>完了日の異なる観測時刻で圧縮詳細だけが先に期限切れになりません。</summary>
    /// <returns>詳細の復元と両レコードの期限削除を実媒体で確認します。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompressedHeaderAndDetailsUseTheSameRetentionDateAsync(bool json)
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state");
        IDurableRepository repository = json ? new JsonFileRepository(path)
                                             : new SqliteRepository(path);
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode((_, _) => ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject(new string('x', 12000))))));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        using WorkflowExecutionJournal journal = new(repository, "a", fixture.Engine);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(fixture.Compiler.Compile(new("history", "history", [WorkflowFixture.Definition("action")], [])).Workflow!).ConfigureAwait(true);
        await journal.FlushAsync().ConfigureAwait(true);
        DurableEntry header = Assert.Single(await repository.ListAsync("peer:a:workflow-results").ConfigureAwait(true));
        DurableEntry detail = Assert.Single(await repository.ListAsync("peer:a:workflow-result-details").ConfigureAwait(true));
        DateTimeOffset now = report.CompletedAt.AddDays(2);
        Assert.Equal(report.CompletedAt, HistoryRetentionPolicy.RecordedAt(header.Value!.Json, now));
        Assert.Equal(report.CompletedAt, HistoryRetentionPolicy.RecordedAt(detail.Value!.Json, now));
        using HistoryArchiveService archive = new(repository);
        DataRetentionSettings policy = new(1, 3);
        for (int index = 0; index < 6; index++)
        {
            await archive.MaintainAsync(new(), policy, now).ConfigureAwait(true);
        }
        using WorkflowExecutionJournal restored = new(repository, "a", fixture.Engine);
        await restored.FlushAsync().ConfigureAwait(true);
        Assert.Null(restored.Problem);
        Assert.Equal(ContractJson.Hash(Assert.Single(journal.Recent())), ContractJson.Hash(Assert.Single(restored.Recent())));
        for (int index = 0; index < 6; index++)
        {
            await archive.MaintainAsync(new(), policy, report.CompletedAt.AddDays(5)).ConfigureAwait(true);
        }
        Assert.Empty(await repository.ListAsync(header.Scope).ConfigureAwait(true));
        Assert.Empty(await repository.ListAsync(detail.Scope).ConfigureAwait(true));
    }

    /// <summary>大量の実行後にも最大256件だけを保存し、同じ結果で書込みを繰り返しません。</summary>
    /// <returns>有限の診断行数と新しい結果の保持です。</returns>
    [Fact]
    public async Task HistoryUsesFixedSlotsAndDoesNotRewriteUnchangedResults()
    {
        MemoryRepository repository = new();
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode((_, _) => ValueTask.FromResult(NodeResult.Success())));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        using WorkflowExecutionJournal journal = new(repository, "a", fixture.Engine);
        Runtime.Workflows.Compilation.CompiledWorkflow plan = fixture.Compiler.Compile(new("history", "history", [WorkflowFixture.Definition("action")], [])).Workflow!;
        for (int index = 0; index < 300; index++)
        {
            await fixture.Engine.ExecuteAsync(plan);
        }
        await journal.FlushAsync();
        Assert.Equal(256, journal.Recent().Count);
        Assert.InRange((await repository.ListAsync("peer:a:workflow-results")).Count, 1, 256);
        using WorkflowExecutionJournal restored = new(repository, "a", fixture.Engine);
        await restored.FlushAsync();
        Assert.Null(restored.Problem);
        Assert.Equal(journal.Recent().Select(ContractJson.Hash), restored.Recent().Select(ContractJson.Hash));
        int writes = repository.Writes;
        await journal.FlushAsync();
        Assert.Equal(writes, repository.Writes);
    }

    /// <summary>開始順と完了順が前後しても、循環保存から外れた既知結果を再記録しません。</summary>
    /// <returns>一件ずつ追加した後の保存回数と、元処理の実行回数です。</returns>
    [Theory]
    [InlineData(255)]
    [InlineData(300)]
    [InlineData(550)]
    public async Task LateCompletionDoesNotRequeuePreviouslyPersistedResultsAsync(int interveningRuns)
    {
        MemoryRepository repository = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int effects = 0;
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode(async (_, cancellation) =>
        {
            if (Interlocked.Increment(ref effects) == 1)
            {
                entered.SetResult();
                await finish.Task.WaitAsync(cancellation).ConfigureAwait(false);
            }
            return NodeResult.Success();
        }));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        using WorkflowExecutionJournal journal = new(repository, "a", fixture.Engine);
        Runtime.Workflows.Compilation.CompiledWorkflow plan = fixture.Compiler.Compile(new("history", "history", [WorkflowFixture.Definition("action")], [])).Workflow!;
        Task<ExecutionReport> delayed = fixture.Engine.ExecuteAsync(plan, cancellationToken: timeout.Token);
        try
        {
            await entered.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
            for (int index = 0; index < interveningRuns; index++)
            {
                await fixture.Engine.ExecuteAsync(plan, cancellationToken: timeout.Token).ConfigureAwait(true);
            }
            await journal.FlushAsync(timeout.Token).ConfigureAwait(true);
        }
        finally
        {
            finish.TrySetResult();
            await delayed.ConfigureAwait(true);
        }
        ExecutionReport completed = await delayed.ConfigureAwait(true);
        Assert.NotNull(fixture.Engine.Find(completed.Id));
        await journal.FlushAsync(timeout.Token).ConfigureAwait(true);
        Assert.Contains(journal.Recent(), view => WorkflowExecutionJournal.Identity(view) == completed.Id);
        await fixture.Engine.ExecuteAsync(plan, cancellationToken: timeout.Token).ConfigureAwait(true);
        await journal.FlushAsync(timeout.Token).ConfigureAwait(true);
        int writes = repository.Writes;
        for (int index = 0; index < 3; index++)
        {
            await journal.FlushAsync(timeout.Token).ConfigureAwait(true);
        }
        Assert.Equal(writes, repository.Writes);
        Assert.Equal(interveningRuns + 2, effects);
        Assert.Equal(256, journal.Recent().Count);
    }

    /// <summary>診断保存の回数だけを観測し、通常のローカル保存規則を使用します。</summary>
    private sealed class MemoryRepository : DurableRepository
    {
        private readonly RepositoryDocument document = new();
        private readonly Lock gate = new();

        internal int Writes
        {
            get; private set;
        }

        /// <summary>媒体が確定できなかった状態を一回だけ模擬します。</summary>
        internal bool Reject
        {
            get; set;
        }

        /// <summary>失敗時は保存全体を未確認とします。</summary>
        /// <returns>変更前の拒否または通常の原子保存です。</returns>
        public override ValueTask<EOutcome> WriteSnapshotsAsync(IReadOnlyList<LocalSnapshotWrite> snapshots, CancellationToken cancellationToken = default)
            => Reject ? ValueTask.FromResult(EOutcome.Unknown)
                      : base.WriteSnapshotsAsync(snapshots, cancellationToken);

        /// <summary>既存の保存規則をそのまま使います。</summary>
        /// <returns>一取引の結果です。</returns>
        private protected override T Access<T>(Func<RepositoryDocument, T> operation, bool write)
        {
            using (gate.EnterScope())
            {
                if (write)
                {
                    Writes++;
                }
                return operation(document);
            }
        }
    }
}
