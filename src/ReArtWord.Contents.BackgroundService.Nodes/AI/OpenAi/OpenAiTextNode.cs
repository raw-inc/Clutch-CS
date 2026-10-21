using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.Media;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Secrets;

namespace ReArtWord.Contents.BackgroundService.Nodes.AI.OpenAi;

/// <summary>Responses APIから完了済みのテキストを取得します。</summary>
internal sealed class OpenAiTextNode : AiOperationNode
{
    /// <summary>Text Generatorの公式接続実装を作ります。</summary>
    internal OpenAiTextNode(ISecretStore secrets, AiOptions options, MediaOptions media) : base(secrets, options, media, "ai.text.generate", "{}", [])
    {
    }

    /// <summary>同期Responsesの最終textだけを結果として取り出します。</summary>
    /// <returns>完了状態の生成テキストです。</returns>
    protected override async Task<NodeResult> PerformAsync(NodeExecutionContext context, AiRequestSession session, CancellationToken cancellationToken = default)
    {
        NodeSettings settings = new(context.Settings);
        JsonElement response = await session.JsonAsync("v1/responses", new { model = settings.ReadText("model"), input = Prompt(context), background = false }, cancellationToken: cancellationToken).ConfigureAwait(false);
        string? status = response.GetProperty("status").GetString();
        session.TerminalKnown = status is "completed" or "failed" or "cancelled" or "incomplete";
        string completed = "completed";
        if (!string.Equals(status, completed, StringComparison.Ordinal))
        {
            throw new InvalidDataException("最終応答が完了していません。");
        }
        session.TerminalKnown = true;
        List<string> parts = [];
        string typeKey = "type";
        foreach (JsonElement item in response.GetProperty("output").EnumerateArray())
        {
            string message = "message";
            if (!string.Equals(item.GetProperty(typeKey).GetString(), message, StringComparison.Ordinal))
            {
                continue;
            }
            foreach (JsonElement content in item.GetProperty("content").EnumerateArray())
            {
                string outputText = "output_text";
                if (string.Equals(content.GetProperty(typeKey).GetString(), outputText, StringComparison.Ordinal))
                {
                    parts.Add(content.GetProperty("text").GetString()!);
                }
            }
        }
        if (parts.Count == 0)
        {
            throw new InvalidDataException("最終テキストがありません。");
        }
        return NodeResult.Success(NodeValue.FromObject(session.Redact(string.Join("\n", parts))));
    }
}
