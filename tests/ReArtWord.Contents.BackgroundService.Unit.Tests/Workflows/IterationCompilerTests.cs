using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>曖昧な反復範囲と既知の型不適合を副作用の前に検出します。</summary>
public sealed class IterationCompilerTests
{
    /// <summary>非選択の枝は、実行先が利用不能でも反復の失敗にしません。</summary>
    /// <returns>選択された処理だけが要素ごとに動きます。</returns>
    [Fact]
    public async Task UnselectedBranchDoesNotRequireItsTargetAsync()
    {
        int calls = 0;
        WorkflowFixture fixture = IterationTests.Create((context, _) =>
        {
            calls++;
            return ValueTask.FromResult(NodeResult.Success(context.Inputs["value"], "false"));
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = IterationTests.Definition(end: false);
        definition = definition with
        {
            Nodes = [.. definition.Nodes, WorkflowFixture.Definition("inactive") with { Target = new(EExecutionScope.Any, ["absent"]) }],
            Wires = [.. definition.Wires, new("body", "value", "inactive", "value", "true")],
        };
        ExecutionReport report = await fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, definition)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        Assert.Equal(3, calls);
    }

    /// <summary>Endなしで非選択枝しかない要素も正常な空処理です。</summary>
    /// <returns>未実行を失敗に読み替えない確認です。</returns>
    [Fact]
    public async Task EntirelyUnselectedBodyIsANoOpAsync()
    {
        int calls = 0;
        WorkflowFixture fixture = IterationTests.Create((_, _) =>
        {
            calls++;
            return ValueTask.FromResult(NodeResult.Success());
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = IterationTests.Definition(end: false);
        definition = definition with { Wires = [new("each", "item", "body", "value", "failure")] };
        ExecutionReport report = await fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, definition)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        Assert.Equal(0, calls);
        Assert.Equal(3, IterationTests.Loop(report).Iteration!.Started);
    }

    /// <summary>End省略の内側反復に外側Endを所有させません。</summary>
    /// <returns>曖昧な収集を実行前に拒否します。</returns>
    [Fact]
    public async Task InnerLoopCannotAbsorbOuterEndAsync()
    {
        WorkflowFixture fixture = IterationTests.Create((context, _) => ValueTask.FromResult(NodeResult.Success(context.Inputs["value"])));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        WorkflowDefinition original = IterationTests.Definition("[[1,2],[3,4]]");
        WorkflowDefinition definition = original with
        {
            Nodes = [original.Nodes[0], WorkflowFixture.Definition("inner", IterationNodeTypes.ForEach), original.Nodes[1], original.Nodes[2]],
            Wires = [new("each", "item", "inner", "value"), new("inner", "item", "body", "value"), new("body", "value", "end", "value"), new("each", "loop", "end", "loop")],
        };
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Null(compiled.Workflow);
        Assert.Contains(compiled.Issues, issue => string.Equals(issue.Code, "iteration.endScope", StringComparison.Ordinal));
    }

    /// <summary>二つの反復に同じ処理範囲を暗黙共有させません。</summary>
    /// <returns>交差の検出です。</returns>
    [Fact]
    public async Task PartiallyOverlappingBodiesAreRejectedAsync()
    {
        WorkflowFixture fixture = IterationTests.Create((context, _) => ValueTask.FromResult(NodeResult.Success(context.Inputs["value"])));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        WorkflowDefinition original = IterationTests.Definition(end: false);
        WorkflowDefinition definition = original with
        {
            Nodes = [.. original.Nodes, original.Nodes[0] with { Id = "other" }],
            Wires = [.. original.Wires, new("other", "item", "body", "trigger")],
        };
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Null(compiled.Workflow);
        Assert.Contains(compiled.Issues, issue => string.Equals(issue.Code, "iteration.crossing", StringComparison.Ordinal));
    }

    /// <summary>通常DAGでも反復完了を介した循環依存は拒否します。</summary>
    /// <returns>一件を開始しない依存検証です。</returns>
    [Fact]
    public async Task CompletionDependencyCannotFeedItsOwnBodyAsync()
    {
        WorkflowFixture fixture = IterationTests.Create((context, _) => ValueTask.FromResult(NodeResult.Success(context.Inputs["value"])));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        WorkflowDefinition original = IterationTests.Definition(end: false);
        WorkflowDefinition definition = original with
        {
            Nodes = [.. original.Nodes, WorkflowFixture.Definition("after")],
            Wires = [.. original.Wires, new("each", "result", "after", "value"), new("after", "value", "body", "trigger")],
        };
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Null(compiled.Workflow);
        Assert.Contains(compiled.Issues, issue => string.Equals(issue.Code, "iteration.dependencyCycle", StringComparison.Ordinal));
    }

    /// <summary>Endには一意な開始側loop接続が必要です。</summary>
    /// <returns>開始の省略とEndの不完全設定を区別します。</returns>
    [Fact]
    public async Task UnpairedEndIsRejectedAsync()
    {
        WorkflowFixture fixture = IterationTests.Create((context, _) => ValueTask.FromResult(NodeResult.Success(context.Inputs["value"])));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = IterationTests.Definition();
        definition = definition with { Wires = definition.Wires.Where(wire => !string.Equals(wire.FromPort, IterationNodeTypes.LoopPort, StringComparison.Ordinal)).ToArray() };
        Assert.Null(fixture.Compiler.Compile(definition).Workflow);
    }

    /// <summary>別枝の成功が選択済みの未実行を隠しません。</summary>
    /// <returns>未開始要素を残した失敗です。</returns>
    [Fact]
    public async Task SuccessfulCollectorDoesNotHideUnexecutedSideBranchAsync()
    {
        int calls = 0;
        WorkflowFixture fixture = IterationTests.Create((context, _) =>
        {
            string blocked = "blocked";
            if (string.Equals(context.NodeId, blocked, StringComparison.Ordinal))
            {
                return ValueTask.FromResult(NodeResult.Failure("safety.stopped", "Not started", EOutcome.NotExecuted));
            }
            calls++;
            return ValueTask.FromResult(NodeResult.Success(context.Inputs["value"]));
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = IterationTests.Definition();
        definition = definition with { Nodes = [.. definition.Nodes, WorkflowFixture.Definition("blocked")], Wires = [.. definition.Wires, new("each", "item", "blocked", "value")] };
        ExecutionReport report = await fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, definition)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, report.Outcome);
        Assert.Equal(1, calls);
        Assert.Equal(1, IterationTests.Loop(report).Iteration!.Started);
    }
}
