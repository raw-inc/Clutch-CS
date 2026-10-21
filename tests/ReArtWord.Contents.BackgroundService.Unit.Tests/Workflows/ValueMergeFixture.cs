using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>値合流の外から観測できる入力と出力を試験します。</summary>
internal static class ValueMergeFixture
{
    /// <summary>入力の到着を変換せず後続へ渡します。</summary>
    /// <returns>観測可能な値出力です。</returns>
    internal static NodeResult Result(NodeValue value) => new(EOutcome.Succeeded, "success", new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = value });

    /// <summary>通常の保存前検証も通してから実行します。</summary>
    /// <returns>問題のないグラフです。</returns>
    internal static CompiledWorkflow Compile(WorkflowFixture fixture, WorkflowDefinition definition)
    {
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        return compiled.Workflow!;
    }

    /// <summary>本番の配送経路から渡された各項目を観測します。</summary>
    internal sealed class Probe(Func<NodeExecutionContext, CancellationToken, ValueTask<NodeResult>> action) : IActionNode
    {
        /// <summary>複数の値を独立した配線から受ける有限ノードです。</summary>
        public NodeDescriptor Descriptor
        {
            get;
        } = NodeSupport.Describe("test.value", "Value", "Test", "{}",
            [new("value"), new("address"), new("number"), new("arguments"), new("enabled")], routes: ["success", "failed"]) with { HasAsyncCompletion = true };

        /// <summary>試験ノードに設定はありません。</summary>
        /// <returns>問題なしです。</returns>
        public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => [];

        /// <summary>実際に配送された入力だけを観測します。</summary>
        /// <returns>試験が指定した処理結果です。</returns>
        public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default) => action(context, cancellationToken);
    }
}
