using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Control.Iteration;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>実グラフで子発火の進捗を更新し、元WFの終了までを外から観測します。</summary>
public sealed class WorkflowProgressLifetimeTests
{
    private static readonly NodePreviewQuery query = new("workflow", ["progress"]);
    private static readonly int[] singleItem = [1];

    /// <summary>同じ子発火の表示を残し、正常・失敗・取消・不明の終了で必ず解放します。</summary>
    /// <returns>既存の実行結果を変更しない表示回収です。</returns>
    [Theory]
    [InlineData(EOutcome.Succeeded)]
    [InlineData(EOutcome.Failed)]
    [InlineData(EOutcome.Cancelled)]
    [InlineData(EOutcome.Unknown)]
    public async Task RootCompletionReleasesChildProgressAsync(EOutcome outcome)
    {
        NodePreviewStore store = new();
        Guid run = Guid.NewGuid();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        ValueMergeFixture.Probe probe = new(async (context, token) =>
        {
            string source = "source";
            if (string.Equals(context.NodeId, source, StringComparison.Ordinal))
            {
                return ValueMergeFixture.Result(NodeValue.FromObject(3));
            }
            while (store.Read(query).Count == 0)
            {
                await Task.Delay(5, token).ConfigureAwait(false);
            }
            Assert.Equal(run, Assert.Single(store.Read(query)).Session!.RunId);
            return outcome == EOutcome.Succeeded ? NodeResult.Success()
                                                 : NodeResult.Failure("test.result", "Controlled result", outcome);
        });
        WorkflowFixture fixture = new(probe, new ValueMergeNode(), new ProgressBarNode(store));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        fixture.Engine.CompleteProgress = (session, _, _) =>
        {
            store.CompleteProgress(session);
            return Task.CompletedTask;
        };
        WorkflowDefinition definition = new("workflow", "Progress lifetime", [Node("source", "test.value"), Node("merge", ValueMergePorts.NodeType),
            Node("progress", "control.progress") with { Inputs = new Dictionary<string, NodeValue> { ["total"] = NodeValue.FromObject(10) } }, Node("sink", "test.value")],
            [new("source", "value", "merge", "input1"), new("merge", "start", "progress", "trigger"), new("merge", "value", "progress", "current"),
             new("merge", "start", "sink", "trigger"), new("merge", "value", "sink", "value")]);
        ExecutionReport result = await fixture.Engine.ExecuteAsync(ValueMergeFixture.Compile(fixture, definition), "source", executionId: run, cancellationToken: timeout.Token).ConfigureAwait(true);
        Assert.Equal(outcome, result.Outcome);
        Assert.Empty(store.Read(query));
        Assert.Empty(fixture.Engine.Active());
    }

    /// <summary>要素の子発火が待機している間は保持し、その子が完了してから要素を回収します。</summary>
    /// <returns>内部の反復IDと元WFの実行識別の両方が保たれる確認です。</returns>
    [Fact]
    public async Task IterationProgressWaitsForItsChildrenAsync()
    {
        NodePreviewStore store = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        ValueMergeFixture.Probe probe = new(async (context, token) =>
        {
            string source = "source";
            if (string.Equals(context.NodeId, source, StringComparison.Ordinal))
            {
                return ValueMergeFixture.Result(NodeValue.FromObject(singleItem));
            }
            entered.TrySetResult();
            await release.Task.WaitAsync(token).ConfigureAwait(false);
            return NodeResult.Success();
        });
        WorkflowFixture fixture = new(probe, new ForEachNode(false), new ValueMergeNode(), new ProgressBarNode(store));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        List<ProgressSession> completions = [];
        fixture.Engine.CompleteProgress = (session, _, _) =>
        {
            completions.Add(session);
            store.CompleteProgress(session);
            return Task.CompletedTask;
        };
        WorkflowDefinition definition = new("workflow", "Child progress", [Node("source", "test.value"), Node("loop", IterationNodeTypes.ForEach), Node("merge", ValueMergePorts.NodeType),
            Node("progress", "control.progress") with { Settings = JsonSerializer.SerializeToElement(new { progressScope = "iteration" }), Inputs = new Dictionary<string, NodeValue> { ["total"] = NodeValue.FromObject(1) } }, Node("sink", "test.value")],
            [new("source", "value", "loop", "value"), new("loop", "item", "merge", "input1"), new("merge", "start", "progress", "trigger"), new("merge", "value", "progress", "current"),
             new("merge", "start", "sink", "trigger"), new("merge", "value", "sink", "value")]);
        Task<ExecutionReport> running = fixture.Engine.ExecuteAsync(ValueMergeFixture.Compile(fixture, definition), "source", cancellationToken: timeout.Token);
        ProgressSession? observed = null;
        try
        {
            await entered.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
            while (store.Read(query).Count == 0)
            {
                await Task.Delay(5, timeout.Token).ConfigureAwait(true);
            }
            observed = Assert.Single(store.Read(query)).Session;
            Assert.NotEqual(Guid.Empty, observed!.ScopeId);
            Assert.False(running.IsCompleted);
            Assert.Empty(completions);
        }
        finally
        {
            release.TrySetResult();
        }
        Assert.Equal(EOutcome.Succeeded, (await running.ConfigureAwait(true)).Outcome);
        Assert.Contains(completions, session => session.ScopeId == observed!.ScopeId);
        Assert.Empty(store.Read(query));
    }

    /// <summary>実コンパイラへ渡す最小のノードです。</summary>
    /// <returns>未接続の設定を持たないノードです。</returns>
    private static NodeDefinition Node(string id, string type) => new(id, type, JsonSerializer.SerializeToElement(new { }));
}
