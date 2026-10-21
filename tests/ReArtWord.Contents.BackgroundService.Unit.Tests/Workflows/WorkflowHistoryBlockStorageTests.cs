using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Retention;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting;
using ReArtWord.Contents.BackgroundService.CoreService.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>旧形式との混在、期限境界、欠損時の復元を実際の永続媒体で確認します。</summary>
public sealed class WorkflowHistoryBlockStorageTests
{
    private static string Scope => "peer:a:workflow-results";
    private static string Details => "peer:a:workflow-result-details";

    /// <summary>旧形式と圧縮ブロックが混在しても、結果を失わず期限どおりに削除します。</summary>
    /// <returns>圧縮前後と期限切れの確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedFormatsKeepDetailsAcrossArchivingAndExpireTogetherAsync(bool json)
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state");
        IDurableRepository repository = json ? new JsonFileRepository(path)
                                             : new SqliteRepository(path);
        DateTimeOffset at = new(2026, 10, 8, 23, 59, 59, TimeSpan.Zero);
        WorkflowHistoryRecord[] expected = Enumerable.Range(1, 6).Select(index => Record(index, at.AddMilliseconds(index * 100))).ToArray();
        await repository.WriteLocalSnapshotAsync(Scope, "1", ContractJson.ToValue(expected[0])).ConfigureAwait(true);
        WorkflowHistoryRecord single = WorkflowHistoryCodec.Encode(expected[1]);
        Assert.NotNull(single.Payload);
        await ((ILocalSnapshotBatches)repository).WriteSnapshotsAsync([
            new(Scope, "2", ContractJson.ToValue(single)), new(Details, "2", ContractJson.ToValue(single.Payload)),
        ]).ConfigureAwait(true);
        WorkflowHistoryBlock block = WorkflowHistoryCodec.Pack(expected[2..])!;
        await ((ILocalSnapshotBatches)repository).WriteSnapshotsAsync([
            new(Scope, "6", ContractJson.ToValue(WorkflowHistoryCodec.BlockHeader(expected[^1]))), new(Details, "6", ContractJson.ToValue(block)),
        ]).ConfigureAwait(true);
        WorkflowFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        using HistoryArchiveService archive = new(repository);
        for (int phase = 0; phase < 2; phase++)
        {
            using WorkflowExecutionJournal restored = new(repository, "a", fixture.Engine);
            await restored.FlushAsync().ConfigureAwait(true);
            Assert.Null(restored.Problem);
            Assert.Equal(expected.Reverse().Select(record => ContractJson.Hash(record.View)), restored.Recent().Select(ContractJson.Hash));
            for (int index = 0; index < 6; index++)
            {
                await archive.MaintainAsync(new(), new(1, 3), at.AddDays(2)).ConfigureAwait(true);
            }
        }
        for (int index = 0; index < 6; index++)
        {
            await archive.MaintainAsync(new(), new(1, 3), at.AddDays(5)).ConfigureAwait(true);
        }
        Assert.Empty(await repository.ListAsync(Scope).ConfigureAwait(true));
        Assert.Empty(await repository.ListAsync(Details).ConfigureAwait(true));
        Assert.Empty(fixture.Engine.RecentViews());
    }

    /// <summary>ヘッダーだけ残った場合や別世代の本文を参照する場合に、復元済みと表示しません。</summary>
    /// <returns>正常なブロックへの差替えだけで再実行せずに復元できます。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrWrongGenerationBlockIsReportedAndRecoverableAsync(bool wrongGeneration)
    {
        using MediaTestFiles files = new();
        SqliteRepository repository = new(Path.Combine(files.Root, "state.db"));
        DateTimeOffset at = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        WorkflowHistoryRecord[] records = [Record(1, at), Record(2, at.AddMilliseconds(10))];
        WorkflowHistoryBlock block = WorkflowHistoryCodec.Pack(records)!;
        await repository.WriteLocalSnapshotAsync(Scope, "2", ContractJson.ToValue(WorkflowHistoryCodec.BlockHeader(records[^1]))).ConfigureAwait(true);
        if (wrongGeneration)
        {
            await repository.WriteLocalSnapshotAsync(Details, "2", ContractJson.ToValue(block with { FirstSequence = 3 })).ConfigureAwait(true);
        }
        WorkflowFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        using WorkflowExecutionJournal journal = new(repository, "a", fixture.Engine);
        await journal.FlushAsync().ConfigureAwait(true);
        Assert.Equal("Execution.HistoryPersistenceUnconfirmed", journal.Problem);
        Assert.Empty(journal.Recent());
        await repository.WriteLocalSnapshotAsync(Details, "2", ContractJson.ToValue(block)).ConfigureAwait(true);
        await journal.FlushAsync().ConfigureAwait(true);
        Assert.Null(journal.Problem);
        Assert.Equal(records.Reverse().Select(record => ContractJson.Hash(record.View)), journal.Recent().Select(ContractJson.Hash));
        Assert.Empty(fixture.Engine.RecentViews());
    }

    /// <summary>失敗と結果不明は成功グループへ隠さず、旧readerでも個別に判別できます。</summary>
    /// <returns>実ランタイムが生成した両異常の保存確認です。</returns>
    [Fact]
    public async Task UnsuccessfulExecutionsRemainIndividualRecordsAsync()
    {
        using MediaTestFiles files = new();
        SqliteRepository repository = new(Path.Combine(files.Root, "state.db"));
        int index = 0;
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode((_, _) =>
        {
            EOutcome outcome = ++index switch { 6 => EOutcome.Failed, 7 => EOutcome.Unknown, _ => EOutcome.Succeeded };
            return ValueTask.FromResult(new NodeResult(outcome, "observed", new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(new string('x', 6000)) }));
        }));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        Runtime.Workflows.Compilation.CompiledWorkflow plan = fixture.Compiler.Compile(new("history", "history", [WorkflowFixture.Definition("action")], [])).Workflow!;
        for (int run = 0; run < 12; run++)
        {
            await fixture.Engine.ExecuteAsync(plan).ConfigureAwait(true);
        }
        using WorkflowExecutionJournal journal = new(repository, "a", fixture.Engine);
        await journal.FlushAsync().ConfigureAwait(true);
        Assert.Null(journal.Problem);
        WorkflowHistoryRecord[] headers = (await repository.ListAsync(Scope).ConfigureAwait(true))
            .Select(entry => entry.Value!.Json.Deserialize<WorkflowHistoryRecord>(ContractJson.Options)!).ToArray();
        foreach (EOutcome outcome in new[] { EOutcome.Failed, EOutcome.Unknown })
        {
            WorkflowHistoryRecord header = Assert.Single(headers, item => WorkflowHistoryCodec.Header(item.View).Outcome == outcome);
            Assert.NotEqual(WorkflowHistoryCodec.BlockReason, header.View.DetailReason);
        }
        using WorkflowExecutionJournal restarted = new(repository, "a", fixture.Engine);
        await restarted.FlushAsync().ConfigureAwait(true);
        Assert.Null(restarted.Problem);
        Assert.Equal(journal.Recent().Select(ContractJson.Hash), restarted.Recent().Select(ContractJson.Hash));
    }

    /// <summary>大小の保存グループで循環スロットを何度も再利用し、最新256件を失いません。</summary>
    /// <returns>グループ途中の保持端と、再利用後の再起動による全件照合です。</returns>
    [Fact]
    public async Task RecycledBlocksPreserveEveryRecentExecutionAsync()
    {
        using MediaTestFiles files = new();
        SqliteRepository repository = new(Path.Combine(files.Root, "state.db"));
        int effects = 0;
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode((_, _) =>
            ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject(new { item = ++effects, detail = new string('x', 6000) })))));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        Runtime.Workflows.Compilation.CompiledWorkflow plan = fixture.Compiler.Compile(new("history", "history", [WorkflowFixture.Definition("action")], [])).Workflow!;
        using WorkflowExecutionJournal journal = new(repository, "a", fixture.Engine);
        int[] widths = [1, 31, 7, 17];
        int expected = 0;
        for (int batch = 0; batch < 128; batch++)
        {
            int count = batch < 48 ? 16
                                   : widths[batch % widths.Length];
            expected += count;
            for (int item = 0; item < count; item++)
            {
                await fixture.Engine.ExecuteAsync(plan).ConfigureAwait(true);
            }
            await journal.FlushAsync().ConfigureAwait(true);
            Assert.Null(journal.Problem);
            if (batch % 11 == 0 || batch == 127)
            {
                using WorkflowExecutionJournal restarted = new(repository, "a", fixture.Engine);
                await restarted.FlushAsync().ConfigureAwait(true);
                Assert.Null(restarted.Problem);
                Assert.Equal(journal.Recent().Select(ContractJson.Hash), restarted.Recent().Select(ContractJson.Hash));
            }
        }
        Assert.Equal(expected, effects);
        Assert.Equal(256, journal.Recent().Count);
        Assert.InRange((await repository.ListAsync(Scope).ConfigureAwait(true)).Count, 1, 256);
        Assert.InRange((await repository.ListAsync(Details).ConfigureAwait(true)).Count, 1, 256);
    }

    /// <summary>保持境界を指定できる、識別子と本文を持つ正常実行です。</summary>
    /// <returns>単体でも圧縮される診断です。</returns>
    private static WorkflowHistoryRecord Record(long sequence, DateTimeOffset completed)
    {
        NodeResult result = NodeResult.Success(NodeValue.FromObject(new string('x', 6000)));
        ExecutionReport report = new(Guid.NewGuid(), "flow", "revision", EOutcome.Succeeded, completed.AddMilliseconds(-10), completed,
            [new("action", EOutcome.Succeeded, [new("a", result)])]);
        return new(sequence, new(report));
    }
}
