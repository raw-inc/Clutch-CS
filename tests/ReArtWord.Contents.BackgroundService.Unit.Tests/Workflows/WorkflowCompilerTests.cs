using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>不正な設定・接続が副作用実行へ進まないことを検証します。</summary>
public sealed class WorkflowCompilerTests
{
    /// <summary>個別操作が同名のGroup全体へ広がる定義を保存前に拒否します。</summary>
    [Fact]
    public void NodeIdentifierCannotAlsoNameAnotherNodesSafetyGroup()
    {
        WorkflowCompiler compiler = new(new NodeRegistry([new TestNode()]));
        NodeDefinition single = new("single", "test", NodeSettings.Schema("{}"));
        NodeDefinition grouped = new("other", "test", NodeSettings.Schema("{}"), Safety: new NodeSafetyPolicy(Group: "single"));
        CompilationResult result = compiler.Compile(new("workflow", "collision", [single, grouped], []));
        Assert.Null(result.Workflow);
        Assert.Contains(result.Issues, issue => string.Equals(issue.Code, "safety.scopeCollision", StringComparison.Ordinal));
        Assert.NotNull(compiler.Compile(new("workflow", "separate", [single, grouped with { Safety = new(Group: "range") }], [])).Workflow);
    }

    /// <summary>同一入力の二つの接続元と循環を開始前に検出します。</summary>
    [Fact]
    public void ConflictingInputsAndCyclesAreRejected()
    {
        WorkflowCompiler compiler = new(new NodeRegistry([new TestNode()]));
        NodeDefinition first = new("a", "test", NodeSettings.Schema("{}"));
        NodeDefinition second = new("b", "test", NodeSettings.Schema("{}"));
        WorkflowDefinition definition = new("workflow", "test", [first, second],
        [
            new WireDefinition("a", "value", "b", "value"),
            new WireDefinition("a", "value", "b", "value"),
            new WireDefinition("b", "value", "a", "value"),
        ]);

        CompilationResult result = compiler.Compile(definition);

        Assert.Null(result.Workflow);
        Assert.Contains(result.Issues, issue => string.Equals(issue.Code, "input.multiple", StringComparison.Ordinal));
        Assert.Contains(result.Issues, issue => string.Equals(issue.Code, "wire.cycle", StringComparison.Ordinal));
    }

    /// <summary>実行PC未指定は許可し、場所依存ノードの設定型不正は引き続き拒否します。</summary>
    [Fact]
    public void UnspecifiedPcIsAllowedButWrongSettingTypesAreRejected()
    {
        TestNode node = new(EExecutionLocation.ExplicitPeer);
        WorkflowCompiler compiler = new(new NodeRegistry([node]));
        WorkflowDefinition definition = new("workflow", "test",
            [new NodeDefinition("a", "test", NodeSettings.Schema("""{"amount":"wrong"}"""))], []);

        CompilationResult result = compiler.Compile(definition);

        Assert.DoesNotContain(result.Issues, issue => string.Equals(issue.Code, "target.required", StringComparison.Ordinal));
        Assert.Contains(result.Issues, issue => string.Equals(issue.Code, "settings.value", StringComparison.Ordinal));
        WorkflowDefinition valid = definition with { Nodes = [definition.Nodes[0] with { Settings = NodeSettings.Schema("""{"amount":1}""") }] };
        Assert.NotNull(compiler.Compile(valid).Workflow);
    }

    /// <summary>元のノード配列を変更しても開始済みの計画は変わりません。</summary>
    [Fact]
    public void CompiledDefinitionOwnsItsInputs()
    {
        NodeDefinition[] nodes = [new("a", "test", NodeSettings.Schema("{}"))];
        WorkflowCompiler compiler = new(new NodeRegistry([new TestNode()]));

        CompiledWorkflow plan = compiler.Compile(new WorkflowDefinition("workflow", "test", nodes, [])).Workflow!;
        nodes[0] = nodes[0] with { Id = "changed" };

        Assert.Equal("a", plan.Definition.Nodes[0].Id);
        Assert.Equal("a", plan.OrderedNodes[0].Id);
    }

    /// <summary>検証だけに使う最小のノードです。</summary>
    private sealed class TestNode : IActionNode
    {
        /// <summary>実行場所の条件を指定します。</summary>
        public TestNode(EExecutionLocation location = EExecutionLocation.Independent)
        {
            Descriptor = new NodeDescriptor("test", "検証", "Test", "検証用", ENodeKind.Action, location,
                [new PortDescriptor("value")], [new PortDescriptor("value")], ["success"],
                NodeSettings.Schema("""{"type":"object","properties":{"amount":{"type":"integer"}}}"""));
        }

        /// <summary>検証用の定義です。</summary>
        public NodeDescriptor Descriptor
        {
            get;
        }

        /// <summary>固有設定の追加検証はありません。</summary>
        /// <returns>問題なしです。</returns>
        public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => [];

        /// <summary>正常な値を返します。</summary>
        /// <returns>最小の正常結果です。</returns>
        public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default) => ValueTask.FromResult(NodeResult.Success(NodeValue.Null));
    }
}
