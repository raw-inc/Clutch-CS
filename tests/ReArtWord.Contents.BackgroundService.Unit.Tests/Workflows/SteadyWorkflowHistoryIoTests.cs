using System.Diagnostics;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.CoreService.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Testing.Performance;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>実ランタイムとSQLiteの循環履歴をつなぎ、速度・I/O・復元と無変更時の保存を測定します。</summary>
[Collection("Performance")]
public sealed class SteadyWorkflowHistoryIoTests(ITestOutputHelper output)
{
    /// <summary>高頻度の小さいWFと大きいWFで、本文を保った履歴の確定費用を比較します。</summary>
    /// <returns>実行と診断保存を分けた計測、再起動後の復元確認です。</returns>
    [Theory, Trait("Category", "Performance")]
    [InlineData(24)]
    [InlineData(73)]
    [InlineData(191)]
    public async Task RepeatedExecutionKeepsHistoryBoundedAndRestorableAsync(int nodes)
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository repository = new(path);
        await SteadyWorkflowHistorySeed.CreateAsync(repository, path).ConfigureAwait(true);
        int effects = 0;
        int activeNodes = Math.Max(2, nodes / 3);
        WorkflowFixture fixture = new(new ConstantValueNode(), new WorkflowFixture.ActionNode((context, _) =>
        {
            NodeValue value = context.Inputs.GetValueOrDefault("value") ?? NodeValue.FromObject(new { item = Interlocked.Increment(ref effects), text = "received" });
            return ValueTask.FromResult(NodeResult.Success(value));
        }));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        NodeDefinition[] definitions = [.. Enumerable.Range(0, activeNodes).Select(index => WorkflowFixture.Definition("step-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture))),
            .. Enumerable.Range(activeNodes, nodes - activeNodes)
            .Select(index => WorkflowFixture.Definition("configuration-node-" + index.ToString("D5", System.Globalization.CultureInfo.InvariantCulture), "value.constant", """{"text":"retained setting"}"""))];
        WireDefinition[] wires = Enumerable.Range(1, activeNodes - 1)
            .Select(index => new WireDefinition(definitions[index - 1].Id, "value", definitions[index].Id, "value")).ToArray();
        CompilationResult compilation = fixture.Compiler.Compile(new("steady", "Steady workload", definitions, wires));
        Assert.Empty(compilation.Issues);
        CompiledWorkflow plan = compilation.Workflow!;
        using WorkflowExecutionJournal journal = new(repository, "a", fixture.Engine);
        List<object> measurements = [];
        string? configuredRounds = Environment.GetEnvironmentVariable("CONTENTS_PERFORMANCE_JOURNAL_ROUNDS");
        int rounds = 3;
        if (configuredRounds is not null)
        {
            rounds = int.Parse(configuredRounds, System.Globalization.CultureInfo.InvariantCulture);
            ArgumentOutOfRangeException.ThrowIfLessThan(rounds, 3);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(rounds, 20);
        }
        for (int phase = 0; phase < rounds; phase++)
        {
            ProcessResourceSample before = ProcessResourceSample.Capture(Environment.ProcessId);
            long allocated = GC.GetTotalAllocatedBytes(true);
            int[] collections = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
            Stopwatch elapsed = Stopwatch.StartNew();
            List<double> execution = [], persistence = [];
            for (int batch = 0; batch < 20; batch++)
            {
                for (int item = 0; item < 15; item++)
                {
                    Stopwatch watch = Stopwatch.StartNew();
                    ExecutionReport report = await fixture.Engine.ExecuteAsync(plan).ConfigureAwait(true);
                    execution.Add(watch.Elapsed.TotalMilliseconds);
                    Assert.Equal(EOutcome.Succeeded, report.Outcome);
                    NodeExecutionReport last = Assert.Single(report.Nodes, node => string.Equals(node.NodeId, definitions[activeNodes - 1].Id, StringComparison.Ordinal));
                    Assert.Equal(effects, Assert.Single(last.Targets).Result.Outputs["value"].Json.GetProperty("item").GetInt32());
                }
                Stopwatch save = Stopwatch.StartNew();
                await journal.FlushAsync().ConfigureAwait(true);
                persistence.Add(save.Elapsed.TotalMilliseconds);
                Assert.Null(journal.Problem);
            }
            elapsed.Stop();
            ProcessResourceSample after = ProcessResourceSample.Capture(Environment.ProcessId);
            long? written = after.WriteBytes - before.WriteBytes;
            if (written.HasValue)
            {
                Assert.InRange(written.Value, 0, 16L * 1024 * 1024);
            }
            measurements.Add(new { phase, elapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds, executionMeanMs = execution.Average(),
                executionP95Ms = execution.Order().ElementAt((int)(execution.Count * .95)), flushMeanMs = persistence.Average(),
                allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated, processReadBytes = after.ReadBytes - before.ReadBytes,
                processWriteBytes = written, cpuMilliseconds = after.CpuMilliseconds - before.CpuMilliseconds,
                gen0 = GC.CollectionCount(0) - collections[0], gen1 = GC.CollectionCount(1) - collections[1], gen2 = GC.CollectionCount(2) - collections[2] });
        }
        Assert.Equal(300 * rounds, effects);
        Assert.Equal(256, journal.Recent().Count);
        ProcessResourceSample unchangedBefore = ProcessResourceSample.Capture(Environment.ProcessId);
        await journal.FlushAsync().ConfigureAwait(true);
        ProcessResourceSample unchangedAfter = ProcessResourceSample.Capture(Environment.ProcessId);
        Assert.Equal(unchangedBefore.WriteBytes, unchangedAfter.WriteBytes);
        using WorkflowExecutionJournal restarted = new(repository, "a", fixture.Engine);
        await restarted.FlushAsync().ConfigureAwait(true);
        Assert.Null(restarted.Problem);
        Assert.Equal(journal.Recent().Select(ContractJson.Hash), restarted.Recent().Select(ContractJson.Hash));
        string label = Environment.GetEnvironmentVariable("CONTENTS_PERFORMANCE_LABEL") ?? "current";
        if (label.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new ArgumentException("計測labelには英数字とハイフンだけを指定してください。");
        }
        string reportPath = Path.Combine(Path.GetDirectoryName(path)!, "journal-" + label + "-" + nodes.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".json");
        string json = JsonSerializer.Serialize(new { label, nodes, activeNodes, rounds, executions = effects, scale = Environment.GetEnvironmentVariable("CONTENTS_PERFORMANCE_JOURNAL_SCALE"),
            databaseBytes = new FileInfo(path).Length, measurements });
        await File.WriteAllTextAsync(reportPath, json).ConfigureAwait(true);
        output.WriteLine(json);
        output.WriteLine(reportPath);
    }
}
