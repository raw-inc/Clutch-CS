using System.Text.Json;

namespace ReArtWord.Contents.BackgroundService.Nodes.AI.OpenAi;

/// <summary>推論やツール要求を生成テキストと混同せず、非完了を検出します。</summary>
internal static class OpenAiGenerationOutput
{
    /// <summary>各APIの終端と拒否を確認し、本文を取り出します。</summary>
    /// <returns>ツールだけの応答では空の本文です。元JSONは別出力に保持します。</returns>
    internal static string Text(JsonElement response, bool responses, AiRequestSession session)
    {
        if (responses)
        {
            string? status = response.GetProperty("status").GetString();
            session.TerminalKnown = status is "completed" or "failed" or "cancelled" or "incomplete";
            string completed = "completed";
            if (!string.Equals(status, completed, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The response is not complete.");
            }
            List<string> text = [];
            string message = "message", typeKey = "type";
            foreach (JsonElement item in response.GetProperty("output").EnumerateArray())
            {
                if (string.Equals(item.GetProperty(typeKey).GetString(), message, StringComparison.Ordinal))
                {
                    text.Add(Content(item.GetProperty("content")));
                }
            }
            return string.Join("\n", text);
        }
        JsonElement choices = response.GetProperty("choices");
        if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
        {
            throw new InvalidDataException("Chat returned no choices.");
        }
        List<string> parts = [];
        foreach (JsonElement choice in choices.EnumerateArray())
        {
            string? reason = choice.GetProperty("finish_reason").GetString();
            session.TerminalKnown = reason is not null;
            string[] accepted = ["stop", "tool_calls", "function_call"];
            if (!accepted.Contains(reason, StringComparer.Ordinal))
            {
                throw new InvalidDataException("The chat completion was interrupted.");
            }
            JsonElement message = choice.GetProperty("message");
            string refusalKey = "refusal", contentKey = "content";
            if (message.TryGetProperty(refusalKey, out JsonElement refusal) && refusal.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(refusal.GetString()))
            {
                throw new InvalidDataException("The generation was refused.");
            }
            if (message.TryGetProperty(contentKey, out JsonElement content))
            {
                parts.Add(Content(content));
            }
        }
        return string.Join("\n", parts);
    }

    /// <summary>本文配列からテキストだけを採用します。</summary>
    /// <returns>生成された本文です。</returns>
    private static string Content(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString()!;
        }
        if (content.ValueKind == JsonValueKind.Null)
        {
            return string.Empty;
        }
        List<string> text = [];
        foreach (JsonElement part in content.EnumerateArray())
        {
            string? type = part.GetProperty("type").GetString();
            string refused = "refusal";
            if (string.Equals(type, refused, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The generation was refused.");
            }
            string[] supported = ["text", "output_text"];
            if (supported.Contains(type, StringComparer.Ordinal))
            {
                text.Add(part.GetProperty("text").GetString()!);
            }
        }
        return string.Join("\n", text);
    }
}
