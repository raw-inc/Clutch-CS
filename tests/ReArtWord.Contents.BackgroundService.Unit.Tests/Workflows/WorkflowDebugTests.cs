using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Debugging;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Control.Iteration;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.State;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>次の一件だけの診断、値の有限化、元要求の継続を外から確認します。</summary>
public sealed class WorkflowDebugTests
{
    /// <summary>手動開始の入力があっても、到着しなかった接続を到着済みと表示しません。</summary>
    /// <returns>入力の概要と各配線の到達状態を区別する確認です。</returns>
    [Fact]
    public async Task EntryValueDoesNotMarkAnInactiveConnectionAvailableAsync()
    {
        WorkflowFixture fixture = Fixture();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        CompiledWorkflow workflow = ValueMergeFixture.Compile(fixture, new("route-debug", "Route debug", [Node("source"), Node("sink")],
            [new("source", "value", "sink", "value", When: "failed")]));
        WorkflowDebugArm arm = Arm(fixture, workflow.Definition.Id);
        fixture.Engine.Debug.Claim = (_, _, _) => ValueTask.FromResult<bool?>(true);
        Assert.True(fixture.Engine.Debug.Prepare(arm));
        _ = await fixture.Engine.ExecuteAsync(workflow, "sink", Input(NodeValue.FromObject(99))).ConfigureAwait(true);
        WorkflowDebugView captured = fixture.Engine.Debug.Read(new(arm.Id))!;
        WorkflowDebugNode result = Assert.Single(captured.Nodes!, row => row.NodeId is "sink");
        Assert.Equal("99", result.Inputs["value"]);
        Assert.False(Assert.Single(result.Connections).Available);
    }

    /// <summary>大量反復でも診断の件数を有限にし、記録上限で業務処理を止めません。</summary>
    /// <returns>部分記録の明示、ページング、全要素の処理完了です。</returns>
    [Fact]
    public async Task CaptureLimitDoesNotStopIterationAsync()
    {
        int calls = 0;
        WorkflowFixture fixture = new(new ForEachNode(), new ValueMergeFixture.Probe((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return ValueTask.FromResult(NodeResult.Success());
        }));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        NodeDefinition loop = Node("loop", IterationNodeTypes.ForEach) with { Inputs = Input(NodeValue.FromObject(Enumerable.Range(0, 4096).ToArray())) };
        CompiledWorkflow workflow = ValueMergeFixture.Compile(fixture, new("bounded-debug", "Bounded debug", [loop, Node("sink")], [new("loop", "item", "sink", "value")]));
        WorkflowDebugArm arm = Arm(fixture, workflow.Definition.Id);
        fixture.Engine.Debug.Claim = (_, _, _) => ValueTask.FromResult<bool?>(true);
        Assert.True(fixture.Engine.Debug.Prepare(arm));
        ExecutionReport report = await fixture.Engine.ExecuteAsync(workflow, "loop").ConfigureAwait(true);
        Assert.True(report.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(report));
        Assert.Equal(4096, calls);
        WorkflowDebugView captured = fixture.Engine.Debug.Read(new(arm.Id, Limit: 1))!;
        Assert.True(captured.Truncated);
        Assert.InRange(captured.TotalNodes, 1, 4096);
        Assert.Single(captured.Nodes!);
        Assert.NotNull(captured.NextOffset);
        WorkflowDebugView next = fixture.Engine.Debug.Read(new(arm.Id, captured.NextOffset.Value, 1))!;
        Assert.NotEqual(captured.Nodes![0].InvocationId, Assert.Single(next.Nodes!).InvocationId);
    }

    /// <summary>無効時は取得権へ問い合わせず、通常実行だけが進みます。</summary>
    /// <returns>既定無効の動作です。</returns>
    [Fact]
    public async Task DisabledCaptureDoesNotAskForAdmissionAsync()
    {
        WorkflowFixture fixture = Fixture();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        int calls = 0;
        fixture.Engine.Debug.Claim = (_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return ValueTask.FromResult<bool?>(true);
        };
        ExecutionReport report = await fixture.Engine.ExecuteAsync(Plan(fixture), "source", Input(NodeValue.FromObject(3))).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        Assert.Equal(0, calls);
        Assert.Null(fixture.Engine.Debug.Read(new(Guid.NewGuid())));
    }

    /// <summary>合流した後続まで一回に含め、後の要求や並行要求を混ぜません。</summary>
    /// <returns>値の秘匿と、バイナリのサイズだけの記録も確認します。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CapturesOnlyOneRootAndItsChildrenAsync(bool binary)
    {
        WorkflowFixture fixture = Fixture();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        int admitted = 0;
        fixture.Engine.Debug.Claim = (_, _, _) => ValueTask.FromResult<bool?>(Interlocked.CompareExchange(ref admitted, 1, 0) == 0);
        CompiledWorkflow workflow = Plan(fixture);
        WorkflowDebugArm arm = Arm(fixture, workflow.Definition.Id);
        Assert.True(fixture.Engine.Debug.Prepare(arm));
        NodeValue value = NodeValue.FromObject(new { address = "/audio/request", arguments = new[] { new { type = "s", value = "03" } }, Authorization = "Bearer never-record-this-token" });
        if (binary)
        {
            value = NodeValue.FromBytes(new byte[1024 * 1024]);
        }
        ExecutionReport[] reports = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => fixture.Engine.ExecuteAsync(workflow, "source", Input(value)))).ConfigureAwait(true);
        Assert.All(reports, report => Assert.Equal(EOutcome.Succeeded, report.Outcome));
        WorkflowDebugView captured = fixture.Engine.Debug.Read(new(arm.Id, Limit: 50))!;
        Assert.Equal(EWorkflowDebugState.Completed, captured.State);
        Assert.NotNull(captured.ExecutionId);
        Assert.Contains(captured.Nodes!, node => node.NodeId is "sink" && node.ParentExecutionId == captured.ExecutionId);
        Assert.Contains(captured.Nodes!, node => node.Connections.Any(connection => connection.FromNode is "source" && connection.Available && connection.Inherited));
        Guid[] unrelated = reports.Select(report => report.Id).Where(id => id != captured.ExecutionId).ToArray();
        Assert.DoesNotContain(captured.Nodes!, node => unrelated.Contains(node.ExecutionId));
        string json = JsonSerializer.Serialize(captured);
        Assert.DoesNotContain("never-record-this-token", json, StringComparison.Ordinal);
        if (binary)
        {
            Assert.Contains("Binary (1048576 bytes)", json, StringComparison.Ordinal);
            Assert.InRange(json.Length, 1, 20000);
        }
        else
        {
            Assert.Contains("/audio/request", json, StringComparison.Ordinal);
            Assert.Contains("03", json, StringComparison.Ordinal);
            Assert.Contains("[redacted]", json, StringComparison.Ordinal);
        }
        Assert.True(fixture.Engine.Debug.Prepare(arm));
        _ = await fixture.Engine.ExecuteAsync(workflow, "source", Input(NodeValue.FromObject(99))).ConfigureAwait(true);
        Assert.Equal(json, JsonSerializer.Serialize(fixture.Engine.Debug.Read(new(arm.Id, Limit: 50))));
    }

    /// <summary>予約配布中の入力は予約を消費せず、準備完了後の一件を取得します。</summary>
    /// <returns>タイマーや通信タイミングで次回予約を失わない確認です。</returns>
    [Fact]
    public async Task PreparingAdmissionDoesNotConsumeTheReservationAsync()
    {
        WorkflowFixture fixture = Fixture();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        CompiledWorkflow workflow = Plan(fixture);
        WorkflowDebugArm arm = Arm(fixture, workflow.Definition.Id);
        Assert.True(fixture.Engine.Debug.Prepare(arm));
        fixture.Engine.Debug.Claim = (_, _, _) => ValueTask.FromResult<bool?>(null);
        _ = await fixture.Engine.ExecuteAsync(workflow, "source", Input(NodeValue.FromObject(1))).ConfigureAwait(true);
        Assert.Null(fixture.Engine.Debug.Read(new(arm.Id)));
        fixture.Engine.Debug.Claim = (_, _, _) => ValueTask.FromResult<bool?>(true);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(workflow, "source", Input(NodeValue.FromObject(2))).ConfigureAwait(true);
        Assert.Equal(report.Id, fixture.Engine.Debug.Read(new(arm.Id))!.ExecutionId);
    }

    /// <summary>記録の停止や診断取得権の障害で、業務処理を停止・再実行しません。</summary>
    /// <returns>停止後も元の結果が確認できることです。</returns>
    [Fact]
    public async Task StoppingCaptureDoesNotStopTheWorkflowAsync()
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        WorkflowFixture fixture = new(new ValueMergeFixture.Probe(async (context, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token).ConfigureAwait(false);
            return ValueMergeFixture.Result(context.Inputs["value"]);
        }));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        CompiledWorkflow workflow = ValueMergeFixture.Compile(fixture, new("stop-debug", "Stop debug", [Node("source")], []));
        WorkflowDebugArm arm = Arm(fixture, workflow.Definition.Id);
        fixture.Engine.Debug.Claim = (_, _, _) => ValueTask.FromResult<bool?>(true);
        Assert.True(fixture.Engine.Debug.Prepare(arm));
        Task<ExecutionReport> running = fixture.Engine.ExecuteAsync(workflow, "source", Input(NodeValue.FromObject(42)));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            fixture.Engine.Debug.Cancel(arm.Id);
            Assert.False(running.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await running.ConfigureAwait(true);
        }
        Assert.Equal(EOutcome.Succeeded, (await running.ConfigureAwait(true)).Outcome);
        Assert.Equal("debug.stopped", fixture.Engine.Debug.Read(new(arm.Id))!.Reason);
        WorkflowDebugArm next = Arm(fixture, workflow.Definition.Id);
        fixture.Engine.Debug.Claim = (_, _, _) => ValueTask.FromException<bool?>(new IOException("Debug owner is unavailable"));
        Assert.True(fixture.Engine.Debug.Prepare(next));
        Assert.Equal(EOutcome.Succeeded, (await fixture.Engine.ExecuteAsync(workflow, "source", Input(NodeValue.FromObject(3))).ConfigureAwait(true)).Outcome);
        Assert.False(fixture.Engine.Debug.Prepare(next with { Id = Guid.NewGuid(), ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) }));
    }

    /// <summary>実ノード配送と合流の通常経路を使います。</summary>
    /// <returns>副作用のない隔離エンジンです。</returns>
    private static WorkflowFixture Fixture() => new(new ValueMergeFixture.Probe((context, _) => ValueTask.FromResult(ValueMergeFixture.Result(context.Inputs["value"]))), new SignalMergeNode("each", new TestStateStore()));

    /// <summary>元入力と開始を分けて合流後へ渡す配線です。</summary>
    /// <returns>検証済みの定義です。</returns>
    private static CompiledWorkflow Plan(WorkflowFixture fixture) => ValueMergeFixture.Compile(fixture, new("debug", "Debug", [Node("source"), Node("merge", "control.signal.each"), Node("sink")],
        [new("source", CompletionPorts.Signal, "merge", "input1"), new("merge", "start", "sink", "trigger"), new("source", "value", "sink", "value")]));

    /// <summary>一時記録の所有実体を固定します。</summary>
    /// <returns>新しい一回分のチケットです。</returns>
    private static WorkflowDebugArm Arm(WorkflowFixture fixture, string workflow) => new(Guid.NewGuid(), workflow, fixture.Peers.LocalPeerId, fixture.Peers.LocalInstanceId, DateTimeOffset.UtcNow.AddMinutes(10));

    /// <summary>一件分の入力値です。</summary>
    /// <returns>所有する入力です。</returns>
    private static Dictionary<string, NodeValue> Input(NodeValue value) => new(StringComparer.Ordinal) { ["value"] = value };

    /// <summary>空設定の試験ノードです。</summary>
    /// <returns>有限処理の定義です。</returns>
    private static NodeDefinition Node(string id, string type = "test.value") => WorkflowFixture.Definition(id, type);
}
