using System.Text.Json.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.AI.OpenAi;

/// <summary>ChatとResponsesの任意設定をAPI名への対応とともに管理します。</summary>
internal static class OpenAiGenerationSettings
{
    /// <summary>両APIが受け取る共通オプションです。</summary>
    internal static IReadOnlyList<(string Setting, string Api)> Common
    {
        get;
    } =
        [("temperature", "temperature"), ("topP", "top_p"), ("store", "store"), ("tools", "tools"), ("toolChoice", "tool_choice"), ("parallelToolCalls", "parallel_tool_calls"), ("metadata", "metadata"), ("serviceTier", "service_tier")];

    /// <summary>Chat固有の任意body項目です。</summary>
    internal static IReadOnlyList<(string Setting, string Api)> Chat
    {
        get;
    } =
        [("maxCompletionTokens", "max_completion_tokens"), ("frequencyPenalty", "frequency_penalty"), ("presencePenalty", "presence_penalty"), ("reasoningEffort", "reasoning_effort"), ("responseFormat", "response_format"), ("stop", "stop"), ("seed", "seed"), ("n", "n")];

    /// <summary>Responses固有の任意body項目です。</summary>
    internal static IReadOnlyList<(string Setting, string Api)> Responses
    {
        get;
    } =
        [("instructions", "instructions"), ("maxOutputTokens", "max_output_tokens"), ("previousResponseId", "previous_response_id"), ("conversation", "conversation"), ("prompt", "prompt"), ("reasoning", "reasoning"), ("text", "text"), ("truncation", "truncation"), ("include", "include"), ("maxToolCalls", "max_tool_calls")];

    /// <summary>任意項目にノード独自の既定値を設定しません。</summary>
    /// <returns>モデルに依存する項目は明示時だけ送る設定スキーマです。</returns>
    internal static string Properties(bool responses)
    {
        JsonObject properties = JsonNode.Parse("""
            {"endpoint":{"type":"string","description":"Complete API endpoint URL. Leave unset to use the official endpoint."},
             "temperature":{"type":"number","minimum":0,"maximum":2},"topP":{"type":"number","minimum":0,"maximum":1},
             "store":{"type":"boolean"},"tools":{"type":"array","items":{"type":"object"}},"toolChoice":{"type":["string","object"]},
             "parallelToolCalls":{"type":"boolean"},"metadata":{"type":"object"},"serviceTier":{"type":"string","minLength":1}}
            """)!.AsObject();
        string extras = responses ? """
            {"instructions":{"type":"string"},"maxOutputTokens":{"type":"integer","minimum":1},"previousResponseId":{"type":"string","minLength":1},
             "conversation":{"type":["string","object"]},"prompt":{"type":"object"},"reasoning":{"type":"object"},"text":{"type":"object"},
             "truncation":{"type":"string","enum":["auto","disabled"]},"include":{"type":"array","items":{"type":"string"}},"maxToolCalls":{"type":"integer","minimum":1}}
            """
                                  : """
            {"maxCompletionTokens":{"type":"integer","minimum":1},"frequencyPenalty":{"type":"number","minimum":-2,"maximum":2},
             "presencePenalty":{"type":"number","minimum":-2,"maximum":2},"reasoningEffort":{"type":"string"},"responseFormat":{"type":"object"},
             "stop":{"type":["string","array"]},"seed":{"type":"integer"},"n":{"type":"integer","minimum":1}}
            """;
        foreach ((string key, JsonNode? value) in JsonNode.Parse(extras)!.AsObject())
        {
            properties[key] = value!.DeepClone();
        }
        return properties.ToJsonString();
    }
}
