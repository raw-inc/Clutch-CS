using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>入れ子の版固定・親取消・同時呼出しの引数分離を確認します。</summary>
public sealed class SubworkflowLifetimeTests
{
    /// <summary>呼出し中に孫を削除しても、その一回は取得済みの版で最後まで実行します。</summary>
    /// <returns>削除後の新規呼出しだけが拒否される確認です。</returns>
    [Fact]
    public async Task NestedDefinitionIsCapturedBeforeAwaitingEarlierNodes()
    {
        WorkflowFixture fixture = SubworkflowTests.Create();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowDefinition leaf = new("leaf", "Leaf", [SubworkflowTests.Field("out", true, "answer", true, "old")], []);
        await fixture.Sources.ApplyAsync(SubworkflowTests.Compile(fixture, leaf));
        NodeDefinition delay = new("delay", "control.delay", JsonSerializer.SerializeToElement(new { milliseconds = 30000 }));
        NodeDefinition inner = new("inner", SubworkflowInterface.CallType(leaf.Id), JsonSerializer.SerializeToElement(new { }));
        WorkflowDefinition child = new("child", "Child", [delay, inner, SubworkflowTests.Field("out", true, "answer")],
            [new("delay", "$completed", "inner", "trigger"), new("inner", "answer", "out", "value")]);
        await fixture.Sources.ApplyAsync(SubworkflowTests.Compile(fixture, child));
        NodeDefinition outer = new("outer", SubworkflowInterface.CallType(child.Id), JsonSerializer.SerializeToElement(new { }));
        CompiledWorkflow parent = SubworkflowTests.Compile(fixture, new("parent", "Parent", [outer], []));
        TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Dispatcher.Handler = async (request, token) =>
        {
            string delayId = RecipeNodeIdentity.Child("outer", "delay");
            if (string.Equals(request.Node.Id, delayId, StringComparison.Ordinal))
            {
                reached.TrySetResult();
                await release.Task.WaitAsync(token).ConfigureAwait(false);
                return NodeResult.Success() with { Outputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["$completed"] = NodeValue.FromObject(true) } };
            }
            return await fixture.Executor.ExecuteAsync(request, token).ConfigureAwait(false);
        };
        Task<ExecutionReport> running = fixture.Engine.ExecuteAsync(parent);
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.Sources.RemoveAsync(leaf.Id);
        }
        finally
        {
            release.TrySetResult();
        }
        ExecutionReport result = await running.ConfigureAwait(true);
        Assert.True(result.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(result));
        Assert.Equal("old", SubworkflowTests.Output(result, "outer", "out").GetString());
        Assert.Null(fixture.Compiler.Compile(parent.Definition).Workflow);
    }

    /// <summary>同じ子を同時に呼んでも、引数と返却値を別実行へ混入しません。</summary>
    /// <returns>複数の実エンジン呼出しの結果です。</returns>
    [Fact]
    public async Task ConcurrentCallsKeepTheirOwnArguments()
    {
        WorkflowFixture fixture = SubworkflowTests.Create();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowDefinition child = new("child", "Child", [SubworkflowTests.Field("input", false, "text"), SubworkflowTests.Field("out", true, "answer")], [new("input", "value", "out", "value")]);
        await fixture.Sources.ApplyAsync(SubworkflowTests.Compile(fixture, child));
        Task<ExecutionReport>[] calls = Enumerable.Range(0, 8).Select(index =>
        {
            NodeDefinition node = new("call", SubworkflowInterface.CallType(child.Id), JsonSerializer.SerializeToElement(new { }),
                new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["text"] = NodeValue.FromObject(index.ToString(System.Globalization.CultureInfo.InvariantCulture)) });
            return fixture.Engine.ExecuteAsync(SubworkflowTests.Compile(fixture, new("parent", "Parent", [node], [])));
        }).ToArray();
        ExecutionReport[] results = await Task.WhenAll(calls);
        for (int index = 0; index < results.Length; index++)
        {
            Assert.True(results[index].Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(results[index]));
            Assert.Equal(index.ToString(System.Globalization.CultureInfo.InvariantCulture), SubworkflowTests.Output(results[index], "call", "out").GetString());
        }
    }

    /// <summary>親の取消を待機中の子へ伝え、親の完了後に子を残しません。</summary>
    /// <returns>親子とも所有一覧から解放される確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParentCancellationStopsTheChild(bool beforeAdmission)
    {
        WorkflowFixture fixture = SubworkflowTests.Create();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        NodeDefinition delay = new("delay", "control.delay", JsonSerializer.SerializeToElement(new { milliseconds = 30000 }));
        WorkflowDefinition child = new("child", "Child", [SubworkflowTests.Field("input", false, "text", true, "hello"), delay], [new("input", "value", "delay", "value")]);
        await fixture.Sources.ApplyAsync(SubworkflowTests.Compile(fixture, child));
        NodeDefinition node = new("call", SubworkflowInterface.CallType(child.Id), JsonSerializer.SerializeToElement(new { }));
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Dispatcher.Handler = async (request, token) =>
        {
            string delayId = RecipeNodeIdentity.Child("call", "delay");
            if (beforeAdmission && string.Equals(request.Node.Id, delayId, StringComparison.Ordinal))
            {
                started.TrySetResult();
                await release.Task.ConfigureAwait(false);
            }
            Task<NodeResult> execution = fixture.Executor.ExecuteAsync(request, token);
            if (string.Equals(request.Node.Id, delayId, StringComparison.Ordinal))
            {
                started.TrySetResult();
            }
            return await execution.ConfigureAwait(false);
        };
        Guid id = Guid.NewGuid();
        using CancellationTokenSource cancelled = new();
        Task<ExecutionReport> running = fixture.Engine.ExecuteAsync(SubworkflowTests.Compile(fixture, new("parent", "Parent", [node], [])), executionId: id, cancellationToken: cancelled.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            try
            {
                await cancelled.CancelAsync();
            }
            finally
            {
                release.TrySetResult();
            }
        }
        ExecutionReport result = await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Outcome != EOutcome.Succeeded, JsonSerializer.Serialize(result));
        Assert.Empty(fixture.Engine.Active());
        Assert.Empty(fixture.Executor.Running);
    }
}
