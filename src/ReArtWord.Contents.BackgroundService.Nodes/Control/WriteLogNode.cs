using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;

namespace ReArtWord.Contents.BackgroundService.Nodes.Control;

/// <summary>ログを記録する単独のノードです。</summary>
public sealed class WriteLogNode : IActionNode
{
    private readonly IWorkflowLog dependency;

    /// <summary>共通の観測境界を設定します。</summary>
    public WriteLogNode(IWorkflowLog dependency)
    {
        ArgumentNullException.ThrowIfNull(dependency);
        this.dependency = dependency;
    }

    /// <summary>設定と入出力を公開します。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("control.log", "ログを記録", "Control", """{"type":"object","properties":{"level":{"type":"string","enum":["NOLOG","DEBUG","INFO","WARN","ERROR","FATAL"]},"message":{"type":"string"},"personal":{"type":"boolean"}}}""", [new PortDescriptor("value"), new("message", EPortValueKind.Json, OverridesSetting: "message")]) with
    {
        HasAsyncCompletion = true,
        Description = "message入力の文字列はそのまま、数値・真偽値・null・オブジェクト・配列はJSONでログ本文にします。未接続なら設定messageを使い、入力valueと因果ID、個人情報指定を維持します。NOLOGは記録せず元入力を返して後続へ進みます。",
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
        string level = settings.ReadText("level", "INFO")!;
        string disabled = "NOLOG";
        if (string.Equals(level, disabled, StringComparison.Ordinal))
        {
            return NodeResult.Success(value);
        }
        string messagePort = "message";
        string message = settings.ReadText(messagePort, "")!;
        if (context.Inputs.TryGetValue(messagePort, out NodeValue? supplied))
        {
            if (supplied.Kind != EValueKind.Json || supplied.Json.ValueKind == JsonValueKind.Undefined)
            {
                return NodeResult.Failure("log.message.type", "ログ本文には文字列またはJSONで表現できる値を指定してください。", EOutcome.NotExecuted);
            }

            // Previewと同じ表現を使い、オブジェクト・配列の構造と数値精度を保持します。
            message = supplied.Json.ValueKind == JsonValueKind.String ? supplied.Json.GetString()!
                                                                      : supplied.Json.GetRawText();
        }
        await dependency.WriteAsync(new WorkflowLogEntry(Guid.NewGuid(), context.TimeProvider.GetUtcNow(), level, context.PeerId, message, context.WorkflowId, context.NodeId, context.CausationId, value, settings.ReadBoolean("personal"), Hooks: context.Hooks), cancellationToken).ConfigureAwait(false);
        return NodeResult.Success(value);
    }
}
