using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Media;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Secrets;

namespace ReArtWord.Contents.BackgroundService.Nodes.AI.OpenAi;

/// <summary>OpenAIの二つのテキストAPIを独立した公開契約で実行します。</summary>
internal sealed class OpenAiGenerationNode : AiOperationNode
{
    private readonly bool responses;

    /// <summary>Responsesでは保存promptや前回応答による入力省略を許します。</summary>
    internal OpenAiGenerationNode(ISecretStore secrets, AiOptions options, MediaOptions media, bool responses) : base(secrets, options, media,
        responses ? "ai.openai.responses"
                  : "ai.openai.chat", OpenAiGenerationSettings.Properties(responses), [], category: "TextGeneration",
        outputs: [new("value") { DataType = new("string") }, new("response") { DataType = new("object") }],
        inputRequired: !responses, modelRequired: !responses, allowAnonymousEndpoint: true)
    {
        this.responses = responses;
    }

    /// <summary>同時指定できない会話参照を送信前に拒否します。</summary>
    /// <returns>設定上の矛盾です。</returns>
    protected override IReadOnlyList<ValidationIssue> ValidateSettings(NodeDefinition definition)
    {
        List<ValidationIssue> issues = [.. base.ValidateSettings(definition)];
        NodeSettings settings = new(definition.Settings);
        JsonElement conversation = settings.Get("conversation"), previous = settings.Get("previousResponseId");
        if (responses && conversation.ValueKind != JsonValueKind.Undefined && previous.ValueKind != JsonValueKind.Undefined)
        {
            issues.Add(new("ai.responses.conversation", "Use either a conversation or a previous response ID.", definition.Id));
        }
        return issues;
    }

    /// <summary>パスとクエリを含む明示URLをそのまま使います。</summary>
    /// <returns>一回の要求先です。</returns>
    protected override Uri ProviderBaseUri(NodeSettings settings) => AiRequestSettings.Endpoint(settings,
        new Uri(Options.BaseUri(false), responses ? "v1/responses"
                                                  : "v1/chat/completions"));

    /// <summary>構造化入力を保持して同期要求を一回だけ実行します。</summary>
    /// <returns>生成テキストと応答JSONです。</returns>
    protected override async Task<NodeResult> PerformAsync(NodeExecutionContext context, AiRequestSession session, CancellationToken cancellationToken = default)
    {
        NodeSettings settings = new(context.Settings);
        JsonObject body = [];
        AiRequestSettings.Copy(context.Settings, body, ("model", "model"));
        AiRequestSettings.Copy(context.Settings, body, OpenAiGenerationSettings.Common.ToArray());
        AiRequestSettings.Copy(context.Settings, body, (responses ? OpenAiGenerationSettings.Responses
                                                                  : OpenAiGenerationSettings.Chat).ToArray());
        NodeValue? input = NodeSupport.Input(context);
        if (input is not null)
        {
            if (input.Kind != EValueKind.Json || input.Json.ValueKind is not (JsonValueKind.String or JsonValueKind.Array))
            {
                throw new ArgumentException("Supply text or an array of API messages/input items.");
            }
            JsonNode? content = JsonNode.Parse(input.Json.GetRawText());
            if (responses)
            {
                body["input"] = content;
            }
            else
            {
                if (input.Json.ValueKind == JsonValueKind.String)
                {
                    content = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content });
                }
                if (content is not JsonArray messages || messages.Count == 0)
                {
                    throw new ArgumentException("Chat requires at least one message.");
                }
                body["messages"] = content;
            }
        }
        else
        {
            if (!responses)
            {
                throw new ArgumentException("Chat requires messages.");
            }
        }
        JsonElement response = await session.JsonAsync(ProviderBaseUri(settings).AbsoluteUri, body, cancellationToken: cancellationToken).ConfigureAwait(false);
        string text = OpenAiGenerationOutput.Text(response, responses, session);
        return NodeResult.Success() with
        {
            Outputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
            {
                ["value"] = NodeValue.FromObject(session.Redact(text)),
                ["response"] = NodeValue.FromJson(JsonSerializer.Deserialize<JsonElement>(session.Redact(response.GetRawText()))),
            },
        };
    }
}
