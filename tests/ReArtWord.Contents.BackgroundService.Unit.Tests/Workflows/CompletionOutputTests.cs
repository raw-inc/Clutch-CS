using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Control.Iteration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.History;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>共通完了信号と成否を、値の成功経路から独立して供給します。</summary>
public sealed class CompletionOutputTests
{
    private static readonly int[] oneElement = [1];

    /// <summary>反復の開始処理だけで成功とせず、ボディ全体が終わるまで成否をnullに保ちます。</summary>
    /// <returns>要素開始と反復完了の別々の観測です。</returns>
    [Fact]
    public async Task IterationRemainsPendingUntilItsBodyCompletesAsync()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        WorkflowFixture.ActionNode body = new(async (_, token) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token).ConfigureAwait(false);
            return NodeResult.Success();
        });
        WorkflowFixture fixture = new(body, new ForEachNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        NodeDefinition loop = WorkflowFixture.Definition("loop", "control.forEach") with
        {
            Inputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(oneElement) },
        };
        WorkflowDefinition definition = new("iteration-status", "Iteration", [loop, WorkflowFixture.Definition("body")], [new("loop", "start", "body", "trigger")]);
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        Task<ExecutionReport> running = fixture.Engine.ExecuteAsync(compiled.Workflow!, "loop");
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            Assert.Null(CompletionPorts.Status(Assert.Single(fixture.Engine.ValueObservations.Snapshot()).Outcome));
        }
        finally
        {
            release.TrySetResult();
            await running.ConfigureAwait(true);
        }
        Assert.True(CompletionPorts.Status(Assert.Single(fixture.Engine.ValueObservations.Snapshot()).Outcome));
        Assert.DoesNotContain(fixture.Executor.ValueObservations.Snapshot(), value => value.NodeId is "loop");
    }

    /// <summary>確定失敗でも完了信号は発火し、成否はfalseとして別口へ渡します。</summary>
    /// <returns>共通ポートを使う実グラフの結果です。</returns>
    [Theory]
    [InlineData(EOutcome.Succeeded, true)]
    [InlineData(EOutcome.Failed, false)]
    public async Task CompletionAndSuccessAreSeparateAsync(EOutcome outcome, bool success)
    {
        WorkflowFixture.ActionNode operation = new((_, _) => ValueTask.FromResult(new NodeResult(outcome, outcome == EOutcome.Succeeded ? "success"
                                                                                                                                        : "failure", new Dictionary<string, NodeValue>(StringComparer.Ordinal))));
        WorkflowFixture fixture = new(operation, new PassNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = new("completion", "Completion", [WorkflowFixture.Definition("operation"), WorkflowFixture.Definition("consumer", "control.pass")],
            [new("operation", CompletionPorts.Signal, "consumer", "trigger"), new("operation", CompletionPorts.Success, "consumer", "value")]);
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(compiled.Workflow!, "operation").ConfigureAwait(true);
        NodeResult result = Assert.Single(report.Nodes.Single(node => node.NodeId is "consumer").Targets).Result;
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(success, result.Outputs["value"].Json.GetBoolean());
    }

    /// <summary>未実行や結果不明を、処理済みの開始信号にしません。</summary>
    /// <returns>後続を起動しない境界です。</returns>
    [Theory]
    [InlineData(EOutcome.Unknown)]
    [InlineData(EOutcome.NotExecuted)]
    public async Task UnconfirmedOperationsDoNotSignalCompletionAsync(EOutcome outcome)
    {
        int calls = 0;
        WorkflowFixture.ActionNode operation = new((_, _) =>
        {
            calls++;
            return ValueTask.FromResult(NodeResult.Failure("test.unconfirmed", "Unconfirmed", outcome));
        });
        WorkflowFixture fixture = new(operation, new PassNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        NodeDefinition consumer = WorkflowFixture.Definition("consumer", "control.pass") with { Inputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject("done") } };
        WorkflowDefinition definition = new("unknown-completion", "Unknown", [WorkflowFixture.Definition("operation"), consumer], [new("operation", CompletionPorts.Signal, "consumer", "trigger")]);
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(compiled.Workflow!, "operation").ConfigureAwait(true);
        Assert.Equal(1, calls);
        Assert.Equal(EOutcome.NotExecuted, report.Nodes.Single(node => node.NodeId is "consumer").Outcome);
    }

    /// <summary>処理中はnullを観測し、最新実行だけが成否を更新します。</summary>
    [Fact]
    public void RunningObservationDoesNotReuseAnOldCompletedValue()
    {
        NodeValueObservations values = new();
        NodeDefinition node = WorkflowFixture.Definition("node");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        RemoteExecutionRequest first = new(Guid.NewGuid(), Guid.NewGuid(), "workflow", "revision", node, new Dictionary<string, NodeValue>(StringComparer.Ordinal), "peer", Guid.NewGuid(), now.AddMinutes(1), false);
        values.Begin(first, now);
        Assert.Null(CompletionPorts.Status(Assert.Single(values.Snapshot()).Outcome));
        values.Complete(first, NodeResult.Success(), now.AddSeconds(1));
        Assert.True(CompletionPorts.Status(Assert.Single(values.Snapshot()).Outcome));
        RemoteExecutionRequest second = first with { Id = Guid.NewGuid() };
        values.Begin(second, now.AddSeconds(2));
        values.Complete(first, NodeResult.Success(), now.AddSeconds(3));
        Assert.Null(CompletionPorts.Status(Assert.Single(values.Snapshot()).Outcome));
        values.Complete(second, NodeResult.Failure("test", "Failed"), now.AddSeconds(4));
        Assert.False(CompletionPorts.Status(Assert.Single(values.Snapshot()).Outcome));
    }
}
