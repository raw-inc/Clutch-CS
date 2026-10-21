using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.AI;

/// <summary>四媒体の値を保持したまま明示したAI処理先profileへ振り分けます。</summary>
internal sealed class AiDestinationRouterNode : IActionNode
{
    /// <summary>媒体別経路とprofile選択の設定を公開します。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    } = new NodeDescriptor("ai.destination.route", "AI処理先選択", "AI", "媒体種別の明示設定で処理先profileを選択します。", ENodeKind.Action, EExecutionLocation.Independent, [new PortDescriptor("value", Required: true)], [new PortDescriptor("value"), new PortDescriptor("profile")], ["text", "audio", "picture", "video", "notExecuted"], NodeSettings.Schema("""{"type":"object","required":["mediaKind","profiles"],"properties":{"mediaKind":{"type":"string","enum":["text","audio","picture","video"]},"profiles":{"type":"object","properties":{"text":{"type":"string","minLength":1},"audio":{"type":"string","minLength":1},"picture":{"type":"string","minLength":1},"video":{"type":"string","minLength":1}}}}}"""));

    /// <summary>選択媒体に対するprofileの設定漏れを検出します。</summary>
    /// <returns>不正な設定の一覧です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        IReadOnlyList<ValidationIssue> issues = NodeSchemaValidator.Validate(definition, Descriptor);
        if (issues.Count != 0)
        {
            return issues;
        }
        NodeSettings settings = new(definition.Settings);
        string kind = settings.ReadText("mediaKind")!;
        bool configured = settings.Get("profiles").TryGetProperty(kind, out _);
        if (!configured)
        {
            return [new ValidationIssue("ai.profileMissing", "選択した媒体のprofileがありません。", definition.Id, "profiles")];
        }
        return [];
    }

    /// <summary>未解釈の媒体値と選択profileを媒体経路へ返します。</summary>
    /// <returns>秘密や外部通信を含まない処理先選択結果です。</returns>
    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<ValidationIssue> issues = Validate(new NodeDefinition(context.NodeId, Descriptor.Type, context.Settings, context.Inputs));
        context.Inputs.TryGetValue("value", out NodeValue? value);
        if (issues.Count != 0 || value is null)
        {
            return ValueTask.FromResult(NodeResult.Failure("ai.invalidRoute", "媒体、profileまたは入力が未設定です。", EOutcome.NotExecuted));
        }
        NodeSettings settings = new(context.Settings);
        string kind = settings.ReadText("mediaKind")!;
        return ValueTask.FromResult(new NodeResult(EOutcome.Succeeded, kind, new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = value.Copy(), ["profile"] = NodeValue.FromJson(settings.Get("profiles").GetProperty(kind)) }));
    }
}
