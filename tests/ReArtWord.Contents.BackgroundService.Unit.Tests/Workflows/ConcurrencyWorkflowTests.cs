using System.Collections.Concurrent;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.Concurrency;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Concurrency;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>制限区間が実際の後続処理を待ち、全終了経路で枠を返すことを確認します。</summary>
public sealed class ConcurrencyWorkflowTests
{
    /// <summary>複数回のWF実行を制限し、入力値を別の到達と混ぜません。</summary>
    /// <returns>実処理の最大並列数、各入力と枠の回収です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentExecutionsWaitForBodyAndPreserveValuesAsync(bool end)
    {
        using ProbeLimiter limiter = new();
        ConcurrentBag<int> values = [];
        int active = 0;
        WorkflowFixture fixture = Create(async (context, token) =>
        {
            Assert.Equal(1, Interlocked.Increment(ref active));
            int value = context.Inputs["value"].Json.GetInt32();
            await Task.Delay(20, token).ConfigureAwait(false);
            values.Add(value);
            Interlocked.Decrement(ref active);
            return NodeResult.Success(NodeValue.FromObject(value));
        }, limiter);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        CompiledWorkflow workflow = IterationTests.Compile(fixture, Definition(end));
        ExecutionReport[] reports = await Task.WhenAll(Enumerable.Range(0, 5).Select(value => fixture.Engine.ExecuteAsync(workflow, "limit",
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(value) }))).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        Assert.All(reports, report => Assert.Equal(EOutcome.Succeeded, report.Outcome));
        Assert.Equal([0, 1, 2, 3, 4], values.Order());
        Assert.Equal(5, limiter.Released);
        Assert.Equal(0, limiter.Active);
    }

    /// <summary>指定Endの後続は枠を保持せず、End未指定なら最後の後続まで保持します。</summary>
    /// <returns>後続が動いている間の枠数です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EndReleasesBeforeItsContinuationAsync(bool end)
    {
        using ProbeLimiter limiter = new();
        int after = 0;
        WorkflowFixture fixture = Create((context, _) =>
        {
            string afterId = "after";
            if (string.Equals(context.NodeId, afterId, StringComparison.Ordinal))
            {
                Assert.Equal(Convert.ToInt32(!end), limiter.Active);
                after++;
            }
            return ValueTask.FromResult(NodeResult.Success(context.Inputs["value"]));
        }, limiter);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        WorkflowDefinition source = Definition(end);
        WorkflowDefinition definition = source with
        {
            Nodes = [.. source.Nodes, WorkflowFixture.Definition("after")],
            Wires = [.. source.Wires, new(end ? "end"
                                              : "body", "value", "after", "value")],
        };
        ExecutionReport report = await fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, definition)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        Assert.Equal(1, after);
        Assert.Equal(0, limiter.Active);
    }

    /// <summary>失敗、例外、結果不明、取消でも枠を返し、次の実行を妨げません。</summary>
    /// <returns>原因の保持と全終了経路の解放です。</returns>
    [Theory]
    [InlineData(EOutcome.Failed, false)]
    [InlineData(EOutcome.Unknown, false)]
    [InlineData(EOutcome.Cancelled, false)]
    [InlineData(EOutcome.Unknown, true)]
    public async Task NonSuccessAndExceptionReleaseScopeAsync(EOutcome outcome, bool throws)
    {
        using ProbeLimiter limiter = new();
        WorkflowFixture fixture = Create((_, _) => throws ? throw new InvalidDataException("fixture body failure")
                                                          : ValueTask.FromResult(NodeResult.Failure("fixture.body", "fixture", outcome)), limiter);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, Definition(true))).ConfigureAwait(true);
        Assert.True(outcome == report.Outcome, System.Text.Json.JsonSerializer.Serialize(report));
        Assert.Equal(0, limiter.Active);
        Assert.Equal(1, limiter.Released);
        Assert.Equal(outcome, report.Nodes.Single(node => node.NodeId is "limit").Outcome);
    }

    /// <summary>枠を確認できなかった実行は、後続を一度も呼びません。</summary>
    /// <returns>失敗理由と副作用未開始です。</returns>
    [Fact]
    public async Task AdmissionFailureStopsBodyAsync()
    {
        int calls = 0;
        WorkflowFixture fixture = Create((_, _) =>
        {
            calls++;
            return ValueTask.FromResult(NodeResult.Success());
        }, null);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, Definition(true))).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, report.Outcome);
        Assert.Equal(0, calls);
        Assert.Equal("concurrency.unavailable", report.Nodes.Single(node => node.NodeId is "limit").Targets.Single().Result.ReasonCode);
    }

    /// <summary>通常のノード実行器へ制限境界を接続します。</summary>
    /// <returns>本番と同じコンパイラと実行器です。</returns>
    internal static WorkflowFixture Create(Func<NodeExecutionContext, CancellationToken, ValueTask<NodeResult>> handler, IWorkflowConcurrencyLimiter? limiter)
    {
        WorkflowFixture fixture = new(new ConcurrencyLimitNode(), new ConcurrencyLimitNode(true), new WorkflowFixture.ActionNode(handler, true));
        fixture.Engine.ConcurrencyLimiter = limiter;
        return fixture;
    }

    /// <summary>入力値を制限区間へ渡す最小のWFを作ります。</summary>
    /// <returns>開始、後続、任意の終了です。</returns>
    internal static WorkflowDefinition Definition(bool end)
    {
        List<NodeDefinition> nodes = [WorkflowFixture.Definition("limit", ConcurrencyNodeTypes.Start) with
        {
            Inputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(1) },
        }, WorkflowFixture.Definition("body")];
        List<WireDefinition> wires = [new("limit", "value", "body", "value")];
        if (end)
        {
            nodes.Add(WorkflowFixture.Definition("end", ConcurrencyNodeTypes.End));
            wires.Add(new("limit", "scope", "end", "scope"));
            wires.Add(new("body", "value", "end", "value"));
        }
        return new("limited", "Limited", nodes, wires);
    }

    /// <summary>通信以外の境界試験で、取得と解放の実時点を観測します。</summary>
    internal sealed class ProbeLimiter : IWorkflowConcurrencyLimiter, IDisposable
    {
        private readonly SemaphoreSlim available = new(1, 1);
        private int active;
        private int released;

        internal int Active => Volatile.Read(ref active);
        internal int Released => Volatile.Read(ref released);
        internal ConcurrentQueue<WorkflowConcurrencyRequest> Requests
        {
            get;
        } = new();

        /// <summary>一つの枠を待ち、実行器が後で返す所有権を渡します。</summary>
        /// <returns>明示解放まで有効な枠です。</returns>
        // 抑制理由: 試験対象の区間実行器へ所有権を渡し、解放回数を後から検証します。
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "Leaseの所有権は試験対象の実行器へ渡し、回収を検証します。")]
        public async Task<WorkflowConcurrencyAdmission> AcquireAsync(WorkflowConcurrencyRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(request);
            await available.WaitAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref active);
            return new(new Lease(this));
        }

        /// <summary>試験が開始した実行を待ってから排他資源を破棄します。</summary>
        public void Dispose() => available.Dispose();

        /// <summary>二重解放せず、枠の返却回数を観測します。</summary>
        private sealed class Lease(ProbeLimiter owner) : IAsyncDisposable
        {
            private int disposed;

            /// <summary>区間の終了時に次の到達を進めます。</summary>
            /// <returns>同期的な所有解除です。</returns>
            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref disposed, 1) == 0)
                {
                    Interlocked.Decrement(ref owner.active);
                    Interlocked.Increment(ref owner.released);
                    owner.available.Release();
                }
                return ValueTask.CompletedTask;
            }
        }
    }
}
