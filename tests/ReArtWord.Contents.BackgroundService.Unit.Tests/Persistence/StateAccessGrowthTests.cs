using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.Availability;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.State;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Availability;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.State;
using ReArtWord.Contents.BackgroundService.Runtime.State;
using ReArtWord.Contents.BackgroundService.Testing.Performance;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>Stateの一日2万read・3千writeを、履歴規模を変えた実DBへ繰り返します。</summary>
[Collection("Performance"), Trait("Category", "Performance")]
public sealed class StateAccessGrowthTests(ITestOutputHelper output)
{
    /// <summary>累積履歴によって固定回数の読書きが全履歴走査へ退行しないことを測ります。</summary>
    /// <returns>時間、割当、プロセス論理I/O、DB容量の比較です。</returns>
    [Fact]
    public async Task DailyStateAccessStaysBoundedAcrossRetainedHistoryAsync()
    {
        string root = Path.GetFullPath("../../../../../.work/performance/state-" + Guid.NewGuid().ToString("N"), AppContext.BaseDirectory);
        Directory.CreateDirectory(root);
        string database = Path.Combine(root, "state.db");
        SqliteRepository repository = new(database);
        await SeedWorkflowsAsync(repository).ConfigureAwait(true);
        NodeStateStore durable = new(repository, "fixture");
        durable.SharedWriter = async (scope, key, expected, value, token) =>
        {
            IReadOnlyList<DurableMutation> mutations = await repository.StampAsync([new(scope, key, expected, value)], token).ConfigureAwait(false);
            DurableReceipt receipt = await repository.ApplyBatchAsync(Guid.CreateVersion7(), mutations, token).ConfigureAwait(false);
            return new(receipt.Outcome == EOutcome.Succeeded, await durable.ReadAsync(scope, key, token).ConfigureAwait(false), receipt.Outcome, receipt.Reason);
        };
        using AvailabilityService availability = new("fixture", new(), repository, root);
        AvailableNodeStateStore store = new(durable, availability);
        await store.SetAvailableAsync(NodeStateScope.Cluster, "cue", NodeValue.FromObject(0), null, Guid.CreateVersion7()).ConfigureAwait(true);
        List<Measurement> reads = [], writes = [];
        int prepared = 0;
        foreach (int history in new[] { 3000, 30000, 90000 })
        {
            Seed(database, prepared, history);
            prepared = history;
            for (int warm = 0; warm < 30; warm++)
            {
                await store.ReadAvailableAsync(NodeStateScope.Cluster, "cue").ConfigureAwait(true);
            }
            using SqliteConnection observer = new(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            await observer.OpenAsync().ConfigureAwait(true);
            using SqliteCommand version = observer.CreateCommand();
            version.CommandText = "PRAGMA data_version";
            object? original = await version.ExecuteScalarAsync().ConfigureAwait(true);
            using SqliteCommand retained = observer.CreateCommand();
            retained.CommandText = "SELECT COUNT(*) FROM records WHERE kind='receipt'";
            int count = checked((int)(long)(await retained.ExecuteScalarAsync().ConfigureAwait(true))!);
            reads.Add(await MeasureAsync(count, 20000, database, async () =>
            {
                AvailabilityValue current = await store.ReadAvailableAsync(NodeStateScope.Cluster, "cue").ConfigureAwait(true);
                Assert.NotNull(current.Value);
            }).ConfigureAwait(true));
            Assert.Equal(original, await version.ExecuteScalarAsync().ConfigureAwait(true));
            int value = 0;
            writes.Add(await MeasureAsync(count, 3000, database, async () =>
            {
                VersionedState current = (await durable.ReadAsync(NodeStateScope.Cluster, "cue").ConfigureAwait(true))!;
                StateWriteResult result = await durable.CompareExchangeAsync(NodeStateScope.Cluster, "cue", current.Version, NodeValue.FromObject(++value)).ConfigureAwait(true);
                Assert.True(result.Applied, result.Reason);
            }).ConfigureAwait(true));
            Assert.Equal(3000, (await durable.ReadAsync(NodeStateScope.Cluster, "cue").ConfigureAwait(true))!.Value.Json.GetInt32());
        }
        Assert.InRange(reads[^1].AllocatedBytes, 0, reads[0].AllocatedBytes * 2 + 1048576);
        Assert.InRange(writes[^1].AllocatedBytes, 0, writes[0].AllocatedBytes * 2 + 1048576);
        string report = JsonSerializer.Serialize(new { workflows = 40, nodesPerWorkflow = 200, plannedDailyReads = 20000, plannedDailyWrites = 3000, plannedYears = 3, measurements = new { reads, writes } });
        await File.WriteAllTextAsync(Path.Combine(root, "state-growth.json"), report).ConfigureAwait(true);
        output.WriteLine(root);
        output.WriteLine(report);
    }

    /// <summary>他WFの定義がある保存先でも、State読書きが定義全体を復元しないことを確認します。</summary>
    /// <returns>自動実行しない40定義の準備です。</returns>
    private static async Task SeedWorkflowsAsync(SqliteRepository repository, CancellationToken cancellationToken = default)
    {
        List<DurableMutation> mutations = [];
        for (int workflow = 0; workflow < 40; workflow++)
        {
            NodeDefinition[] nodes = Enumerable.Range(0, 200).Select(index => new NodeDefinition("node-" + index, "value.constant",
                JsonSerializer.SerializeToElement(new { value = new string('v', 256) }))).ToArray();
            WorkflowDefinition definition = new("wf-" + workflow, "State cost fixture " + workflow, nodes, []);
            StoredWorkflow stored = new(definition, ContractJson.Hash(definition), DateTimeOffset.UtcNow, EActivationPolicy.Explicit);
            mutations.Add(new("workflows", definition.Id, null, NodeValue.FromObject(stored)));
        }
        Assert.Equal(EOutcome.Succeeded, (await repository.ApplyBatchAsync(Guid.CreateVersion7(), mutations, cancellationToken).ConfigureAwait(true)).Outcome);
    }

    /// <summary>過去の確定結果だけを有限取引で追加し、準備費用を計測と分けます。</summary>
    private static void Seed(string database, int from, int until)
    {
        for (int offset = from; offset < until; offset += 500)
        {
            using SqliteSession session = new(database);
            RepositoryDocument document = new();
            for (int index = offset; index < Math.Min(until, offset + 500); index++)
            {
                Guid operation = Guid.CreateVersion7(DateTimeOffset.UtcNow.AddDays(-30).AddMilliseconds(index));
                DurableEntry entry = new(NodeStateScope.Cluster, "cue", operation.ToString("N"), NodeValue.FromObject(new { cue = index, name = new string('x', 256) }));
                document.Receipts.Add(operation, new(operation, ContractJson.Hash(index), EOutcome.Succeeded, [entry]));
            }
            session.Save(document);
            session.Commit();
        }
    }

    /// <summary>測定結果を書き出す前に、固定回数の費用を確定します。</summary>
    /// <returns>プロセス全体の論理I/Oを含む測定値です。</returns>
    private static async Task<Measurement> MeasureAsync(int history, int count, string database, Func<Task> action, CancellationToken cancellationToken = default)
    {
        long allocated = GC.GetTotalAllocatedBytes(true);
        ProcessResourceSample before = ProcessResourceSample.Capture(Environment.ProcessId);
        long peakPrivateBytes = before.PrivateBytes, peakWorkingSetBytes = before.WorkingSetBytes;
        Stopwatch timer = Stopwatch.StartNew();
        for (int index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await action().ConfigureAwait(true);
            if (index % 256 == 0)
            {
                ProcessResourceSample sample = ProcessResourceSample.Capture(Environment.ProcessId);
                peakPrivateBytes = Math.Max(peakPrivateBytes, sample.PrivateBytes);
                peakWorkingSetBytes = Math.Max(peakWorkingSetBytes, sample.WorkingSetBytes);
            }
        }
        timer.Stop();
        ProcessResourceSample after = ProcessResourceSample.Capture(Environment.ProcessId);
        return new(history, count, timer.Elapsed.TotalMilliseconds, GC.GetTotalAllocatedBytes(true) - allocated,
            after.ReadBytes - before.ReadBytes, after.WriteBytes - before.WriteBytes, new FileInfo(database).Length,
            after.CpuMilliseconds - before.CpuMilliseconds, Math.Max(peakPrivateBytes, after.PrivateBytes), Math.Max(peakWorkingSetBytes, after.WorkingSetBytes));
    }

    /// <summary>履歴の準備を含まない、同じ処理回数の費用です。</summary>
    private sealed record Measurement(int History, int Count, double ElapsedMilliseconds, long AllocatedBytes, long? ReadBytes, long? WriteBytes, long DatabaseBytes,
        double CpuMilliseconds, long PeakPrivateBytes, long PeakWorkingSetBytes);
}
