using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;

namespace ReArtWord.Contents.BackgroundService.Nodes.Control;

/// <summary>値を通過する単独のノードです。</summary>
public sealed class PassNode : IRuntimeValueNode
{
    /// <summary>設定と入出力を公開します。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("control.pass", "値を通過", "Control", """{"type":"object","properties":{}}""", [new PortDescriptor("value")]) with
    {
        IsValueOperation = true,
        Description = "入力 value を変換せず、そのまま後続へ渡します。定数を置く入口や配線の中継に使います。",
    };

    /// <summary>設定を共通スキーマで検証します。</summary>
    /// <returns>不正な設定項目です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        return NodeSupport.Validate(definition, Descriptor);
    }

    /// <summary>一件の入力を処理して元入力を返します。</summary>
    /// <returns>操作自身の結果です。</returns>
    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        NodeResult? invalid = NodeSupport.Invalid(context, this);
        if (invalid is not null)
        {
            return ValueTask.FromResult(invalid);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(NodeResult.Success(NodeSupport.Input(context)));
    }
}
