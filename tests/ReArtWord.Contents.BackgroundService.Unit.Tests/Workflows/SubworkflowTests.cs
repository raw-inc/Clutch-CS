using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Control.Subworkflows;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>公開入出力と通常の実行・取消境界を持つ子Workflowを確認します。</summary>
public sealed class SubworkflowTests
{
    /// <summary>未指定の引数だけにデフォルトを使い、名前付き出力へ値を戻します。</summary>
    /// <returns>保存・公開・呼出しの一巡です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NamedInputsOutputsAndDefaultsUseTheNormalEngine(bool supplied)
    {
        WorkflowFixture fixture = Create();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowDefinition child = new("child", "名前付きの子", [Field("input", false, "text", true, "default"), Field("output", true, "answer")], [new("input", "value", "output", "value")]);
        CompiledWorkflow plan = Compile(fixture, child);
        await fixture.Sources.ApplyAsync(plan);
        string type = SubworkflowInterface.CallType(child.Id);
        Assert.Contains(fixture.Registry.Descriptors, descriptor => string.Equals(descriptor.Type, type, StringComparison.Ordinal));
        Dictionary<string, NodeValue> inputs = new(StringComparer.Ordinal);
        if (supplied)
        {
            inputs["text"] = NodeValue.FromObject("supplied");
        }
        NodeDefinition call = new("call", type, JsonSerializer.SerializeToElement(new { }), inputs);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(Compile(fixture, new("parent", "Parent", [call], [])));
        Assert.True(report.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(report));
        string expected = supplied ? "supplied"
                                   : "default";
        Assert.Equal(expected, Output(report, "call", "output").GetString());
        Assert.DoesNotContain(fixture.Engine.Recent(), item => string.Equals(item.WorkflowId, child.Id, StringComparison.Ordinal));
    }

    /// <summary>必須引数の欠落と型違いを、呼出し側のコンパイルで拒否します。</summary>
    /// <returns>子を実行しない拒否です。</returns>
    [Fact]
    public async Task RequiredInputAndExplicitNullAreNotReplacedByAnUnspecifiedDefault()
    {
        WorkflowFixture fixture = Create();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowDefinition child = new("child", "Child", [Field("in", false, "required")], []);
        await fixture.Sources.ApplyAsync(Compile(fixture, child));
        NodeDefinition call = new("call", SubworkflowInterface.CallType(child.Id), JsonSerializer.SerializeToElement(new { }));
        Assert.Null(fixture.Compiler.Compile(new("parent", "Parent", [call], [])).Workflow);
        call = call with { Inputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["required"] = NodeValue.Null } };
        CompilationResult compilation = fixture.Compiler.Compile(new("parent", "Parent", [call], []));
        if (compilation.Workflow is not null)
        {
            ExecutionReport report = await fixture.Engine.ExecuteAsync(compilation.Workflow);
            Assert.NotEqual(EOutcome.Succeeded, report.Outcome);
        }
    }

    /// <summary>出力だけの定義も公開し、入力なしでデフォルト出力を返せます。</summary>
    /// <returns>境界の片側だけでもSubWorkflowになる確認です。</returns>
    [Fact]
    public async Task OutputOnlyWorkflowIsARecipeAndCanBeCalled()
    {
        WorkflowFixture fixture = Create();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowDefinition child = new("output-only", "Output only", [Field("out", true, "answer", true, "hello")], []);
        Assert.True(SubworkflowInterface.IsSubworkflow(child));
        await fixture.Sources.ApplyAsync(Compile(fixture, child));
        NodeDefinition call = new("call", SubworkflowInterface.CallType(child.Id), JsonSerializer.SerializeToElement(new { }));
        ExecutionReport report = await fixture.Engine.ExecuteAsync(Compile(fixture, new("parent", "Parent", [call], [])));
        Assert.True(report.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(report));
        Assert.Equal("hello", Output(report, "call", "out").GetString());
    }

    /// <summary>自己参照を追加した保存候補を、以前の有効版で誤って検証しません。</summary>
    /// <returns>再帰と重複公開名の事前拒否です。</returns>
    [Fact]
    public async Task RecursiveAndDuplicateInterfacesAreRejected()
    {
        WorkflowFixture fixture = Create();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowDefinition child = new("child", "Child", [Field("out", true, "answer", true, "hello")], []);
        await fixture.Sources.ApplyAsync(Compile(fixture, child));
        WorkflowDefinition recursive = child with { Nodes = [.. child.Nodes, new("self", SubworkflowInterface.CallType(child.Id), JsonSerializer.SerializeToElement(new { }))] };
        Assert.Null(fixture.Compiler.Compile(recursive).Workflow);
        Assert.Null(fixture.Compiler.Compile(child with { Nodes = [.. child.Nodes, Field("duplicate", true, "answer", true, "other")] }).Workflow);
        WorkflowFixture other = Create();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable otherLifetime = other.ConfigureAwait(true);
        Assert.DoesNotContain(other.Registry.Descriptors, descriptor => string.Equals(descriptor.Type, SubworkflowInterface.CallType(child.Id), StringComparison.Ordinal));
    }

    /// <summary>配置先の実行報告から展開後の公開境界の値を読みます。</summary>
    /// <returns>親WFの同じ一件で得た値です。</returns>
    internal static NodeValue Output(ExecutionReport report, string instance, string node)
    {
        string id = RecipeNodeIdentity.Child(instance, node);
        return Assert.Single(Assert.Single(report.Nodes, item => string.Equals(item.NodeId, id, StringComparison.Ordinal)).Targets).Result.Outputs["value"];
    }

    /// <summary>同じ公開名の二方向は許可し、直接配線できる型を持たせます。</summary>
    /// <returns>保存可能な最小公開契約です。</returns>
    internal static NodeDefinition Field(string id, bool output, string name, bool hasDefault = false, string? fallback = null)
        => new(id, output ? SubworkflowInterface.OutputType
                          : SubworkflowInterface.InputType, JsonSerializer.SerializeToElement(new { name, valueType = new { type = "string" }, hasDefault, defaultValue = fallback }));

    /// <summary>共通の実行境界とクラスタ単位の有効版カタログを組み立てます。</summary>
    /// <returns>独立した一クラスタの実行構成です。</returns>
    internal static WorkflowFixture Create()
    {
        WorkflowFixture fixture = new(new SubworkflowBoundaryNode(false), new SubworkflowBoundaryNode(true), new SubstringNode(), new RegexNode(true), new DelayNode());
        fixture.Registry.Dynamic = new SubworkflowCatalog(fixture.Registry, fixture.Sources.GetEffectiveWorkflows);
        return fixture;
    }

    /// <summary>設定不備を実行結果と取り違えない試験用コンパイルです。</summary>
    /// <returns>検証済み計画です。</returns>
    internal static CompiledWorkflow Compile(WorkflowFixture fixture, WorkflowDefinition definition)
    {
        CompilationResult result = fixture.Compiler.Compile(definition);
        Assert.True(result.Workflow is not null, JsonSerializer.Serialize(result.Issues));
        return result.Workflow;
    }
}
