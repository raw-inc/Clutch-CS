using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;

namespace ReArtWord.Contents.BackgroundService.Nodes.Control;

/// <summary>事象を発行する単独のノードです。</summary>
public sealed class PublishEventNode : IActionNode
{
    private readonly IWorkflowEvents dependency;

    /// <summary>共通の観測境界を設定します。</summary>
    public PublishEventNode(IWorkflowEvents dependency)
    {
        ArgumentNullException.ThrowIfNull(dependency);
        this.dependency = dependency;
    }

    /// <summary>設定と入出力を公開します。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("control.publish", "事象を発行", "Control", """{"type":"object","required":["name"],"properties":{"name":{"type":"string","minLength":1}}}""", [new PortDescriptor("value")]) with
    {
        HasAsyncCompletion = true,
        Description = "name で指定した内部イベントを発行します。value をイベントの内容と後続の出力へ渡します。",
    };

    /// <summary>設定を共通スキーマで検証します。</summary>
    /// <returns>不正な設定項目です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        return NodeSupport.Validate(definition, Descriptor);
    }

    /// <summary>一件の入力を処理して元入力を返します。</summary>
    /// <returns>操作自身の結果です。</returns>
    public async ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        NodeResult? invalid = NodeSupport.Invalid(context, this);
        if (invalid is not null)
        {
            return invalid;
        }
        NodeSettings settings = new(context.Settings);
        NodeValue? value = NodeSupport.Input(context);
        await dependency.PublishAsync(new WorkflowEvent(Guid.NewGuid(), settings.ReadText("name")!, context.TimeProvider.GetUtcNow(), context.PeerId, value ?? NodeValue.Null, context.WorkflowId, context.NodeId, context.CausationId, Hooks: context.Hooks), cancellationToken).ConfigureAwait(false);
        return NodeResult.Success(value);
    }
}
