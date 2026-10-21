using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>純粋値の未入力・Binary・失敗結果を再現します。</summary>
internal sealed class SettingsValueProbeNode(NodeResult result) : IValueNode
{
    /// <summary>値出力の有無は実際の結果で決まります。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("test.value", "Value probe", "Test", "{}", inputs: []);

    /// <summary>設定はありません。</summary>
    /// <returns>設定上の問題はありません。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => [];

    /// <summary>外部副作用なしに指定した結果を返します。</summary>
    /// <returns>試験の構築結果です。</returns>
    public NodeResult Evaluate(NodeDefinition definition, IReadOnlyDictionary<string, NodeValue> inputs) => result;

    /// <summary>実行側も同じ値を返します。</summary>
    /// <returns>元の構築結果です。</returns>
    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default) => ValueTask.FromResult(result);
}
