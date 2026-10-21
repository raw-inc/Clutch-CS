using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;

namespace ReArtWord.Contents.BackgroundService.Nodes.Control;

/// <summary>指定時間待機する単独のノードです。</summary>
public sealed class DelayNode : IActionNode
{
    /// <summary>設定と入出力を公開します。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("control.delay", "指定時間待機", "Control", """{"type":"object","required":["milliseconds"],"properties":{"milliseconds":{"type":"number","minimum":0,"maximum":2147483646}}}""", [new PortDescriptor("value")]) with
    {
        HasAsyncCompletion = true,
        Description = "milliseconds で指定した時間だけ待ち、元の入力 value を後続へ渡します。",
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
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(new NodeSettings(context.Settings).ReadNumber("milliseconds")), context.TimeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return NodeResult.Failure("delay.cancelled", "指定時間の待機を取り消しました。", EOutcome.Cancelled);
        }
        return NodeResult.Success(NodeSupport.Input(context));
    }
}
