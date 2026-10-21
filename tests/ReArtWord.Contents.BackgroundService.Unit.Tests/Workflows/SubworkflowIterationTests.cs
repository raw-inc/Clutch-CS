using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Control.Iteration;
using ReArtWord.Contents.BackgroundService.Nodes.Control.Subworkflows;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Unit.Tests.State;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>配置の公開入力が属する反復を、入力未使用の内部処理にも引き継ぐことを確認します。</summary>
public sealed class SubworkflowIterationTests
{
    /// <summary>同じ値が連続しても、引数なし処理は要素ごと・配置ごとに一度ずつ実行します。</summary>
    /// <returns>逐次・並列・入れ子レシピの反復境界です。</returns>
    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(true, false, 1)]
    [InlineData(false, true, 1)]
    [InlineData(true, true, 2)]
    public async Task UnusedPublicInputStillScopesInternalActionsAsync(bool parallel, bool nested, int placements)
    {
        int calls = 0;
        WorkflowFixture fixture = Create((context, _) =>
        {
            Assert.Empty(context.Inputs);
            return ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject(Interlocked.Increment(ref calls).ToString(System.Globalization.CultureInfo.InvariantCulture))));
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        string recipe = await RegisterAsync(fixture, nested).ConfigureAwait(true);
        WorkflowDefinition definition = Parent(recipe, parallel, placements);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, definition)).ConfigureAwait(true);
        Assert.True(report.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(report));
        Assert.Equal(12 * placements, calls);
        string firstEnd = "end0";
        JsonElement collected = report.Nodes.Single(node => string.Equals(node.NodeId, firstEnd, StringComparison.Ordinal)).Targets.Single().Result.Outputs["value"].Json;
        Assert.Equal(12, collected.GetArrayLength());
        Assert.Equal(12, collected.EnumerateArray().Select(item => item.GetString()).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2 + placements, definition.Wires.Count);
    }

    /// <summary>内外の反復が同値でも、最内周の要素ごとに生成し、外側の準備は共有します。</summary>
    /// <returns>入れ子の回数と共有準備の回数です。</returns>
    [Fact]
    public async Task NestedIterationsKeepIndependentGenerationsAndSharedPreparationAsync()
    {
        int generated = 0, prepared = 0;
        WorkflowFixture fixture = Create((context, _) =>
        {
            string preparation = "preparation";
            if (string.Equals(context.NodeId, preparation, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref prepared);
                return ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject("shared")));
            }
            return ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject(Interlocked.Increment(ref generated).ToString(System.Globalization.CultureInfo.InvariantCulture))));
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        string recipe = await RegisterAsync(fixture, false).ConfigureAwait(true);
        NodeDefinition outer = Loop("outer", true, NodeValue.FromObject(Enumerable.Range(0, 3).Select(_ => Enumerable.Repeat("same", 4).ToArray()).ToArray()));
        NodeDefinition inner = Loop("inner", true);
        NodeDefinition call = Call("call", recipe);
        WorkflowDefinition definition = new("nested-iterations", "Nested", [outer, inner, call, End("inner-end"), End("outer-end"), WorkflowFixture.Definition("preparation"), WorkflowFixture.Definition("shared-use", "control.pass")],
            [new("outer", "item", "inner", "value"), new("outer", "loop", "outer-end", "loop"), new("inner", "item", "call", "unused"), new("inner", "loop", "inner-end", "loop"),
                new("call", "answer", "inner-end", "value"), new("inner-end", "value", "outer-end", "value"), new("inner", "start", "shared-use", "trigger"), new("preparation", "value", "shared-use", "value")]);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, definition)).ConfigureAwait(true);
        Assert.True(report.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(report));
        Assert.Equal(12, generated);
        Assert.Equal(1, prepared);
    }

    /// <summary>反復内のレシピにさらに反復があっても、生成処理を内側の要素へ所属させます。</summary>
    /// <returns>配置の反復とレシピ内部の反復を混ぜない確認です。</returns>
    [Fact]
    public async Task RecipeOwnedIterationRemainsNestedInItsPlacementAsync()
    {
        int calls = 0;
        WorkflowFixture fixture = Create((_, _) => ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject(Interlocked.Increment(ref calls).ToString(System.Globalization.CultureInfo.InvariantCulture)))));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        string leaf = await RegisterAsync(fixture, false).ConfigureAwait(true);
        NodeDefinition input = new("input", SubworkflowInterface.InputType, NodeSettings.Schema("{\"name\":\"unused\",\"valueType\":{\"type\":\"array\",\"elementType\":{\"type\":\"string\"}}}"));
        NodeDefinition output = new("output", SubworkflowInterface.OutputType, NodeSettings.Schema("{\"name\":\"answer\",\"valueType\":{\"type\":\"array\",\"elementType\":{\"type\":\"string\"}}}"));
        WorkflowDefinition recipe = new("inner-loop-recipe", "Inner loop", [input, Loop("inner", true), Call("leaf", leaf), End("inner-end"), output],
            [new("input", "value", "inner", "value"), new("inner", "item", "leaf", "unused"), new("inner", "loop", "inner-end", "loop"), new("leaf", "answer", "inner-end", "value"), new("inner-end", "value", "output", "value")]);
        await fixture.Sources.ApplyAsync(SubworkflowTests.Compile(fixture, recipe)).ConfigureAwait(true);
        WorkflowDefinition parent = Parent(recipe.Id, true, 1);
        NodeValue rows = NodeValue.FromObject(Enumerable.Range(0, 3).Select(_ => Enumerable.Repeat("same", 2).ToArray()).ToArray());
        parent = parent with { Nodes = [Loop("each", true, rows), .. parent.Nodes.Skip(1)] };
        ExecutionReport report = await fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, parent)).ConfigureAwait(true);
        Assert.True(report.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(report));
        Assert.Equal(6, calls);
        string end = "end0";
        JsonElement collected = report.Nodes.Single(node => string.Equals(node.NodeId, end, StringComparison.Ordinal)).Targets.Single().Result.Outputs["value"].Json;
        Assert.Equal(3, collected.GetArrayLength());
        Assert.All(collected.EnumerateArray(), row => Assert.Equal(2, row.GetArrayLength()));
    }

    /// <summary>レシピ内の明示開始待ちは、公開入力による自動開始で迂回しません。</summary>
    /// <returns>不成立の内部条件では生成を行わない確認です。</returns>
    [Fact]
    public async Task InternalUnselectedBranchIsNotStartedAsync()
    {
        int calls = 0;
        WorkflowFixture fixture = Create((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject("unexpected")));
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        NodeDefinition condition = WorkflowFixture.Definition("condition", "control.ifStart") with
        {
            Inputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["condition"] = NodeValue.FromObject(false) },
        };
        WorkflowDefinition recipe = new("conditional", "Conditional", [SubworkflowTests.Field("input", false, "unused"), condition, WorkflowFixture.Definition("generate")], [new("condition", "start", "generate", "trigger")]);
        await fixture.Sources.ApplyAsync(SubworkflowTests.Compile(fixture, recipe)).ConfigureAwait(true);
        WorkflowDefinition parent = new("conditional-parent", "Conditional", [Loop("each", false, NodeValue.FromObject(Enumerable.Repeat("same", 2).ToArray())), Call("call", recipe.Id)], [new("each", "item", "call", "unused")]);
        await fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, parent)).ConfigureAwait(true);
        Assert.Equal(0, calls);
    }

    /// <summary>親側で選ばれなかったレシピの内部入口も開始しません。</summary>
    /// <returns>反復値の不成立経路を保つ確認です。</returns>
    [Fact]
    public async Task UnselectedPublicInputDoesNotExecuteTheRecipeAsync()
    {
        int calls = 0;
        WorkflowFixture fixture = Create((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject("unexpected")));
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        NodeDefinition field = new("input", SubworkflowInterface.InputType, NodeSettings.Schema("{\"name\":\"unused\",\"valueType\":{\"type\":\"boolean\"}}"));
        WorkflowDefinition recipe = new("unused-branch", "Unused", [field, WorkflowFixture.Definition("generate")], []);
        await fixture.Sources.ApplyAsync(SubworkflowTests.Compile(fixture, recipe)).ConfigureAwait(true);
        WorkflowDefinition parent = new("unselected-parent", "Unselected", [Loop("each", false, NodeValue.FromObject(new bool[2])), WorkflowFixture.Definition("condition", "control.ifStart"), Call("call", recipe.Id)],
            [new("each", "item", "condition", "condition"), new("condition", "start", "call", "unused")]);
        await fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, parent)).ConfigureAwait(true);
        Assert.Equal(0, calls);
    }

    /// <summary>条件と先着合流を経由するレシピも、同じ反復入力を欠落させません。</summary>
    /// <returns>内部Firstと明示開始線の反復回数です。</returns>
    [Fact]
    public async Task FirstMergeEntryKeepsRecipeIterationInputsAsync()
    {
        int calls = 0;
        WorkflowFixture fixture = Create((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject("generated")));
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        NodeDefinition condition = WorkflowFixture.Definition("condition", "control.ifStart") with
        {
            Inputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["condition"] = NodeValue.FromObject(true) },
        };
        WorkflowDefinition recipe = new("first-recipe", "First", [SubworkflowTests.Field("input", false, "unused"), condition, WorkflowFixture.Definition("first", "control.signal.first"), WorkflowFixture.Definition("generate"), SubworkflowTests.Field("out", true, "answer")],
            [new("condition", "start", "first", "input1"), new("first", "start", "generate", "trigger"), new("generate", "value", "out", "value")]);
        await fixture.Sources.ApplyAsync(SubworkflowTests.Compile(fixture, recipe)).ConfigureAwait(true);
        WorkflowDefinition parent = Parent(recipe.Id, true, 1);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, parent)).ConfigureAwait(true);
        Assert.True(report.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(report));
        Assert.Equal(12, calls);
    }

    /// <summary>空の反復では、通常なら独立入口になる生成処理も実行しません。</summary>
    /// <returns>ゼロ要素の開始境界です。</returns>
    [Fact]
    public async Task EmptyIterationDoesNotStartInternalRootsAsync()
    {
        int calls = 0;
        WorkflowFixture fixture = Create((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject("unexpected")));
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        string recipe = await RegisterAsync(fixture, true).ConfigureAwait(true);
        WorkflowDefinition parent = Parent(recipe, false, 1);
        parent = parent with { Nodes = [Loop("each", false, NodeValue.FromObject(Array.Empty<string>())), .. parent.Nodes.Skip(1)] };
        ExecutionReport report = await fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, parent)).ConfigureAwait(true);
        Assert.True(report.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(report));
        Assert.Equal(0, calls);
    }

    /// <summary>公開入出力と反復を実際のランタイムへ登録します。</summary>
    /// <returns>独立した試験環境です。</returns>
    private static WorkflowFixture Create(Func<NodeExecutionContext, CancellationToken, ValueTask<NodeResult>> handler)
    {
        WorkflowFixture fixture = new(new ForEachNode(), new ForEachNode(true), new ForEachEndNode(), new SubworkflowBoundaryNode(false), new SubworkflowBoundaryNode(true), new IfNode(), new PassNode(), new SignalMergeNode("first", new TestStateStore()), new WorkflowFixture.ActionNode(handler));
        fixture.Registry.Dynamic = new SubworkflowCatalog(fixture.Registry, fixture.Sources.GetEffectiveWorkflows);
        return fixture;
    }

    /// <summary>入力を参照しない生成処理を、必要ならもう一段のレシピへ包みます。</summary>
    /// <returns>配置するレシピの識別子です。</returns>
    private static async Task<string> RegisterAsync(WorkflowFixture fixture, bool nested, CancellationToken cancellationToken = default)
    {
        WorkflowDefinition leaf = new("leaf", "Leaf", [SubworkflowTests.Field("input", false, "unused"), WorkflowFixture.Definition("generate"), SubworkflowTests.Field("out", true, "answer")], [new("generate", "value", "out", "value")]);
        await fixture.Sources.ApplyAsync(SubworkflowTests.Compile(fixture, leaf), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!nested)
        {
            return leaf.Id;
        }
        WorkflowDefinition outer = new("wrapper", "Wrapper", [SubworkflowTests.Field("input", false, "unused"), Call("nested", leaf.Id), SubworkflowTests.Field("out", true, "answer")],
            [new("input", "value", "nested", "unused"), new("nested", "answer", "out", "value")]);
        await fixture.Sources.ApplyAsync(SubworkflowTests.Compile(fixture, outer), cancellationToken: cancellationToken).ConfigureAwait(false);
        return outer.Id;
    }

    /// <summary>同一要素を受ける配置を並べ、開始線なしで結果を集約します。</summary>
    /// <returns>元の編集内容を変更しない試験定義です。</returns>
    private static WorkflowDefinition Parent(string recipe, bool parallel, int placements)
    {
        List<NodeDefinition> nodes = [Loop("each", parallel, NodeValue.FromObject(Enumerable.Repeat("same", 12).ToArray()))];
        List<WireDefinition> wires = [];
        for (int index = 0; index < placements; index++)
        {
            string call = "call" + index, end = "end" + index;
            nodes.Add(Call(call, recipe));
            if (index == 0)
            {
                nodes.Add(End(end));
                wires.Add(new("each", "loop", end, "loop"));
                wires.Add(new(call, "answer", end, "value"));
            }
            wires.Add(new("each", "item", call, "unused"));
        }
        return new("parent", "Parent", nodes, wires);
    }

    /// <summary>配列入力を持つ反復開始です。</summary>
    /// <returns>実行順だけを切り替えた開始ノードです。</returns>
    private static NodeDefinition Loop(string id, bool parallel, NodeValue? value = null)
    {
        string type = IterationNodeTypes.ForEach;
        if (parallel)
        {
            type = IterationNodeTypes.ParallelForEach;
        }
        NodeDefinition node = WorkflowFixture.Definition(id, type);
        if (value is not null)
        {
            node = node with { Inputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = value } };
        }
        return node;
    }

    /// <summary>公開名だけで接続する配置です。</summary>
    /// <returns>追加の開始線を持たないレシピ配置です。</returns>
    private static NodeDefinition Call(string id, string recipe) => WorkflowFixture.Definition(id, SubworkflowInterface.CallType(recipe));

    /// <summary>反復結果を入力順に集約します。</summary>
    /// <returns>通常の反復終了です。</returns>
    private static NodeDefinition End(string id) => WorkflowFixture.Definition(id, IterationNodeTypes.End);
}
