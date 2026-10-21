using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.AI.OpenAi;

/// <summary>APIメッセージを既存配列へ追加する純粋な組立てノードです。</summary>
internal sealed class OpenAiMessageNode : PureValueNode
{
    /// <summary>role/contentを直接配線し、前の一覧へ一件を追加できます。</summary>
    internal OpenAiMessageNode(bool responses)
    {
        string schema = """{"type":"object","properties":{"role":{"type":"string","enum":["user","developer","system","assistant"],"default":"user"}}}""";
        if (!responses)
        {
            schema = """{"type":"object","properties":{"role":{"type":"string","enum":["user","developer","system","assistant","tool"],"default":"user"},"name":{"type":"string","minLength":1},"toolCallId":{"type":"string","minLength":1,"x-visibleWhen":{"property":"role","values":["tool"]},"x-requiredWhen":{"property":"role","values":["tool"]}}}}""";
        }
        Descriptor = NodeSupport.Describe(responses ? "ai.openai.responses.message"
                                                    : "ai.openai.chat.message", "Build API messages", "TextGeneration", schema,
            inputs: [new("messages") { DataType = new("array", new("object")) }, new("content", Required: true)],
            outputs: [new("value") { DataType = new("array", new("object")) }]);
    }

    /// <summary>前の配列とメッセージ本文を分離した入出力です。</summary>
    public override NodeDescriptor Descriptor
    {
        get;
    }

    /// <summary>役割と本文を保持し、元配列を変更せずに構築します。</summary>
    /// <returns>Chat messagesまたはResponses inputへ渡せる配列です。</returns>
    protected override NodeValue Build(NodeDefinition definition, IReadOnlyDictionary<string, NodeValue> inputs)
    {
        string messagesKey = "messages", contentKey = "content";
        JsonArray messages = [];
        if (inputs.TryGetValue(messagesKey, out NodeValue? previous))
        {
            if (previous.Kind != EValueKind.Json || previous.Json.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("messages.array");
            }
            messages = JsonNode.Parse(previous.Json.GetRawText())!.AsArray();
        }
        if (!inputs.TryGetValue(contentKey, out NodeValue? content) || content.Kind != EValueKind.Json || content.Json.ValueKind is not (JsonValueKind.String or JsonValueKind.Array))
        {
            throw new FormatException("message.content");
        }
        NodeSettings settings = new(definition.Settings);
        string role = settings.ReadText("role", "user")!;
        string toolRole = "tool";
        if (string.Equals(role, toolRole, StringComparison.Ordinal) && string.IsNullOrWhiteSpace(settings.ReadText(ToolCallId)))
        {
            throw new FormatException("message.toolCallId.required");
        }
        JsonObject message = new() { ["role"] = role, ["content"] = JsonNode.Parse(content.Json.GetRawText()) };
        AiRequestSettings.Copy(definition.Settings, message, ("name", "name"));
        if (string.Equals(role, toolRole, StringComparison.Ordinal))
        {
            AiRequestSettings.Copy(definition.Settings, message, (ToolCallId, "tool_call_id"));
        }
        messages.Add(message);
        return NodeValue.FromJson(JsonSerializer.SerializeToElement(messages));
    }

    private static string ToolCallId => "toolCallId";
}
