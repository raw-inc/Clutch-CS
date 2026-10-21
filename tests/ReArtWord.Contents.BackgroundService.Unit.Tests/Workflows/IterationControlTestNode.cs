using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>同じ要素に属する複数の値と、完了順を観測する試験用の有限処理です。</summary>
internal sealed class IterationControlTestNode(Func<NodeExecutionContext, CancellationToken, ValueTask<NodeResult>> execute) : IActionNode
{
    /// <summary>複数の結果と条件値を通常の配線で使用します。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("test.iteration", "Iteration result", "Test", "{}",
        [new("value", EPortValueKind.Json, Required: true)], [new("value", EPortValueKind.Json), new("text", EPortValueKind.Json), new("condition", EPortValueKind.Json)]) with { HasAsyncCompletion = true };

    /// <summary>実行契約以外の設定を追加しません。</summary>
    /// <returns>試験固有の検証問題なしです。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => [];

    /// <summary>実ランタイムが渡した入力と寿命を処理へ接続します。</summary>
    /// <returns>要素固有の結果です。</returns>
    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default) => execute(context, cancellationToken);

    /// <summary>同じ要素から、数値・文字列・条件を対応付けて生成します。</summary>
    /// <returns>結果同士の混線を判別できる出力です。</returns>
    internal static NodeResult Result(int value, bool condition = false) => new(Contracts.Execution.EOutcome.Succeeded, "success",
        new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["value"] = NodeValue.FromObject(value),
            ["text"] = NodeValue.FromObject("item-" + value.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ["condition"] = NodeValue.FromObject(condition),
        });
}
