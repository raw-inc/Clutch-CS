using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>失敗、取消、上限、対象変更が反復の成功や暗黙再実行にならないことを確認します。</summary>
public sealed class IterationFailureTests
{
    /// <summary>非成功後は残りを開始せず、不明結果を失敗へ読み替えません。</summary>
    /// <returns>一要素だけを開始した集約です。</returns>
    [Theory]
    [InlineData(EOutcome.Failed, EOutcome.Failed)]
    [InlineData(EOutcome.Unknown, EOutcome.Unknown)]
    [InlineData(EOutcome.NotExecuted, EOutcome.Failed)]
    [InlineData(EOutcome.Cancelled, EOutcome.Cancelled)]
    public async Task FailureStopsUnstartedElementsAsync(EOutcome nodeOutcome, EOutcome expected)
    {
        int calls = 0;
        WorkflowFixture fixture = IterationTests.Create((_, _) =>
        {
            calls++;
            return ValueTask.FromResult(NodeResult.Failure("test.failure", "fixture", nodeOutcome));
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, IterationTests.Definition(end: false))).ConfigureAwait(true);
        Assert.Equal(expected, report.Outcome);
        Assert.Equal(1, calls);
        IterationExecutionSummary summary = IterationTests.Loop(report).Iteration!;
        Assert.Equal(3, summary.Count);
        Assert.Equal(1, summary.Started);
        Assert.Equal(nodeOutcome, Assert.Single(summary.Items).Execution.Report!.Nodes[0].Outcome);
    }

    /// <summary>並列の一要素が失敗しても、既に開始した副作用の結果を待ちます。</summary>
    /// <returns>未開始抑止と進行中結果の確認です。</returns>
    [Fact]
    public async Task ParallelFailureAwaitsAlreadyStartedBodyAsync()
    {
        TaskCompletionSource both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        WorkflowFixture fixture = IterationTests.Create(async (context, token) =>
        {
            Interlocked.Increment(ref calls);
            string valuePort = "value";
            if (context.Inputs[valuePort].Json.GetInt32() == 1)
            {
                await both.Task.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                return NodeResult.Failure("test.failure", "fixture");
            }
            both.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            return NodeResult.Success(context.Inputs["value"]);
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        Task<ExecutionReport> running = fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, IterationTests.Definition(parallel: true)));
        await both.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
        await Task.Delay(30).ConfigureAwait(true);
        Assert.False(running.IsCompleted);
        release.TrySetResult();
        ExecutionReport report = await running.ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, report.Outcome);
        Assert.Equal(2, calls);
        Assert.Equal(2, IterationTests.Loop(report).Iteration!.Started);
    }

    /// <summary>明示取消後に残りの要素を開始しません。</summary>
    /// <returns>停止を確認した結果です。</returns>
    [Fact]
    public async Task CancellationStopsFutureElementsAsync()
    {
        using CancellationTokenSource cancellation = new();
        int calls = 0;
        WorkflowFixture fixture = IterationTests.Create(async (_, token) =>
        {
            calls++;
            await cancellation.CancelAsync().ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                return NodeResult.Success();
            }
            catch (OperationCanceledException)
            {
                return NodeResult.Failure("test.cancelled", "Stopped", EOutcome.Cancelled);
            }
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, IterationTests.Definition(end: false)), cancellationToken: cancellation.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Cancelled, report.Outcome);
        Assert.Equal(1, calls);
    }

    /// <summary>一件開始時の実行Peer実体を全要素で維持します。</summary>
    /// <returns>途中の再参加先へ配送し直さない確認です。</returns>
    [Fact]
    public async Task PeerIdentityIsCapturedOnceForAllElementsAsync()
    {
        WorkflowFixture fixture = IterationTests.Create((context, _) => ValueTask.FromResult(NodeResult.Success(context.Inputs["value"])));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        PeerInfo remote = new("b", Guid.NewGuid(), "b-host", "127.0.0.1", 0, EPeerStatus.Ready, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow);
        fixture.Peers.Others = [remote];
        List<Guid> identities = [];
        fixture.Dispatcher.Handler = (request, token) =>
        {
            string body = "body";
            if (!string.Equals(request.Node.Id, body, StringComparison.Ordinal))
            {
                return fixture.Executor.ExecuteAsync(request, token);
            }
            identities.Add(request.TargetInstanceId);
            fixture.Peers.Others = [remote with { InstanceId = Guid.NewGuid() }];
            return Task.FromResult(NodeResult.Success(request.Inputs["value"]));
        };
        WorkflowDefinition definition = IterationTests.Definition();
        definition = definition with { Nodes = definition.Nodes.Select(node =>
        {
            string body = "body";
            if (string.Equals(node.Id, body, StringComparison.Ordinal))
            {
                return node with { Target = new(EExecutionScope.One, ["b"]) };
            }
            return node;
        }).ToArray() };
        ExecutionReport report = await fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, definition)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        Assert.Equal(3, identities.Count);
        Assert.All(identities, id => Assert.Equal(remote.InstanceId, id));
    }

    /// <summary>大量出力は有限保持で打ち切り、全件完了としません。</summary>
    /// <returns>実開始件数と上限失敗です。</returns>
    [Fact]
    public async Task CollectedOutputHasAnAggregateByteLimitAsync()
    {
        int calls = 0;
        NodeValue large = NodeValue.FromObject(new string('x', 1048576));
        WorkflowFixture fixture = IterationTests.Create((_, _) =>
        {
            calls++;
            return ValueTask.FromResult(NodeResult.Success(large));
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = IterationTests.Definition("[1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20]");
        ExecutionReport report = await fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, definition)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, report.Outcome);
        Assert.InRange(calls, 1, 17);
        Assert.Equal("iteration.outputLimit", Assert.Single(IterationTests.Loop(report).Targets).Result.ReasonCode);
        Assert.True(IterationTests.Loop(report).Iteration!.Started < 20);
    }

    /// <summary>入れ子で乗算される反復にも一件の実行全体の開始上限を適用します。</summary>
    /// <returns>外側の未開始要素を残して停止します。</returns>
    [Fact]
    public async Task NestedIterationCountIsBoundedAcrossTheWholeRunAsync()
    {
        int calls = 0;
        WorkflowFixture fixture = IterationTests.Create((context, _) =>
        {
            calls++;
            return ValueTask.FromResult(NodeResult.Success(context.Inputs["value"]));
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        WorkflowDefinition original = IterationTests.Definition(System.Text.Json.JsonSerializer.Serialize(Enumerable.Range(0, 65)), end: false);
        NodeDefinition inner = WorkflowFixture.Definition("inner", IterationNodeTypes.ForEach) with
        {
            Inputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(Enumerable.Range(0, 64)) },
        };
        WorkflowDefinition definition = original with
        {
            Nodes = [original.Nodes[0], inner, original.Nodes[1]],
            Wires = [new("each", "item", "inner", "trigger"), new("inner", "item", "body", "value")],
        };
        ExecutionReport report = await fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, definition)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, report.Outcome);
        Assert.InRange(calls, 1, 4096);
        Assert.True(IterationTests.Loop(report).Iteration!.Started < 65);
    }
}
