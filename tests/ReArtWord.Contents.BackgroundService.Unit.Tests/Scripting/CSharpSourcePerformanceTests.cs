using System.Diagnostics;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Nodes.Scripting;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Testing.Performance;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Scripting;

/// <summary>実workerからの継続通知を消費済み順に捨て、送出総数と保持メモリを分離して測ります。</summary>
[Collection("Performance"), Trait("Category", "Performance")]
public sealed class CSharpSourcePerformanceTests(ITestOutputHelper output)
{
    /// <summary>同じ16KiB通知を段階的に増やし、pipeの一回性・値・worker解放を確認します。</summary>
    /// <returns>100／1000／10000件の時間、割当、自然なGC後のメモリと論理I/Oです。</returns>
    [Fact]
    public async Task RepeatedNotificationsDoNotRetainConsumedBinaryOrWorkersAsync()
    {
        const string code = """
            public static async IAsyncEnumerable<byte[]> Run(int count, [EnumeratorCancellation] CancellationToken token = default)
            {
                for (int i = 0; i < count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    await Task.Yield();
                    var bytes = new byte[16384];
                    BitConverter.GetBytes(i).CopyTo(bytes, 0);
                    BitConverter.GetBytes(Environment.ProcessId).CopyTo(bytes, 4);
                    yield return bytes;
                }
            }
            """;
        ScriptAnalysis analysis = CSharpScriptCompiler.Analyze(code, continuous: true);
        Assert.Empty(analysis.Diagnostics);
        string settings = JsonSerializer.Serialize(new { code, analysis, maxBytes = 32768 }, ContractJson.Options);
        string root = Path.GetFullPath("../../../../../.work/performance/csharp-source-" + Guid.NewGuid().ToString("N"), AppContext.BaseDirectory);
        Directory.CreateDirectory(root);
        List<object> measurements = [];
        foreach (int count in new[] { 100, 1000, 10000 })
        {
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(90));
            NodeExecutionContext initial = NodeTestContext.Create(settings, extra: new Dictionary<string, NodeValue> { ["arguments"] = NodeValue.FromObject(new { count }) });
            List<ProcessResourceSample> worker = [];
            int received = 0, pid = 0;
            NodeSourceContext source = new(initial, () => initial, (item, _) =>
            {
                ReadOnlySpan<byte> bytes = item.Result.Outputs["value"].Binary.Span;
                Assert.Equal(16384, bytes.Length);
                Assert.Equal(received, BitConverter.ToInt32(bytes));
                int current = BitConverter.ToInt32(bytes[4..]);
                if (received == 0)
                {
                    pid = current;
                }
                Assert.Equal(pid, current);
                if (received == 0 || received % 500 == 0 || received == count - 1)
                {
                    worker.Add(ProcessResourceSample.Capture(pid));
                }
                received++;
                return ValueTask.CompletedTask;
            }, () => false);
            ProcessResourceSample before = ProcessResourceSample.Capture(Environment.ProcessId);
            long allocated = GC.GetTotalAllocatedBytes(true);
            int[] collections = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
            Stopwatch watch = Stopwatch.StartNew();
            NodeResult result = await CSharpSourceTests.Node().RunAsync(source, deadline.Token).ConfigureAwait(true);
            watch.Stop();
            long allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated;
            ProcessResourceSample after = ProcessResourceSample.Capture(Environment.ProcessId);
            Assert.Equal(EOutcome.Succeeded, result.Outcome);
            Assert.Equal(count, received);
            Assert.All(worker, sample => Assert.InRange(sample.PrivateBytes, 0, 512L * 1024 * 1024));
            Assert.InRange(allocatedBytes, 0, 32L * 1024 * 1024 + count * 262144L);
            Assert.DoesNotContain(Process.GetProcessesByName("Clutch-CS.CoreService"), process =>
            {
                using (process)
                {
                    return process.Id == pid && !process.HasExited;
                }
            });
            measurements.Add(new { count, bytesPerItem = 16384, milliseconds = watch.Elapsed.TotalMilliseconds, allocatedBytes,
                cpuMilliseconds = after.CpuMilliseconds - before.CpuMilliseconds, processWriteBytes = after.WriteBytes - before.WriteBytes,
                gen0 = GC.CollectionCount(0) - collections[0], gen1 = GC.CollectionCount(1) - collections[1], gen2 = GC.CollectionCount(2) - collections[2], before, after, worker });
            await File.WriteAllTextAsync(Path.Combine(root, "source-growth.json"), JsonSerializer.Serialize(measurements), deadline.Token).ConfigureAwait(true);
            output.WriteLine(FormattableString.Invariant($"count={count}, elapsed={watch.Elapsed.TotalMilliseconds:F1}ms, allocated={allocatedBytes}"));
        }
        output.WriteLine(root);
    }
}
