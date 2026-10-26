using System.Text.Json.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.AI.ElevenLabs;

/// <summary>公式TTS APIでモデルごとに使える設定を公開します。</summary>
internal static class ElevenLabsSpeechSettings
{
    /// <summary>URLや本文へ渡す音声IDの形式をサービス内で統一します。</summary>
    /// <returns>音声IDに使用できる文字だけを含むかです。</returns>
    internal static bool ValidVoice(string voice) => voice.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    /// <summary>生成物として検証できるMP3形式だけを公開します。</summary>
    internal static string AudioProperties => """
        {"outputPath":{"type":"string","minLength":1},
         "outputFormat":{"type":"string","enum":["mp3_22050_32","mp3_44100_32","mp3_44100_64","mp3_44100_96","mp3_44100_128","mp3_44100_192"]},
         "downloadTimeoutMs":{"type":"integer","minimum":1,"maximum":3600000},"maximumBytes":{"type":"integer","minimum":1,"maximum":2147483647}}
        """;

    /// <summary>未指定のvoice settingsはアカウント側設定を維持します。</summary>
    /// <returns>モデル別ノードのプロパティです。</returns>
    internal static string Properties(bool v3)
    {
        JsonObject properties = JsonNode.Parse(AudioProperties)!.AsObject();
        JsonArray formats = properties["outputFormat"]!["enum"]!.AsArray();
        foreach (string format in new[] { "wav_8000", "wav_16000", "wav_22050", "wav_24000", "wav_32000", "wav_44100", "wav_48000" })
        {
            formats.Add(format);
        }
        properties["outputFormat"]!["description"] = "Select MP3 or WAV and use the matching file extension. WAV at 44.1 kHz requires a Pro plan or above.";
        string model = v3 ? "eleven_v3"
                          : "eleven_multilingual_v2";
        properties["model"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["examples"] = new JsonArray(model), ["default"] = model };
        properties["voice"] = JsonNode.Parse("""{"type":"string","minLength":1,"description":"Voice ID available to this ElevenLabs API key."}""");
        JsonObject common = JsonNode.Parse("""
            {"stability":{"type":"number","minimum":0,"maximum":1},"speed":{"type":"number","minimum":0.7,"maximum":1.2},
             "seed":{"type":"integer","minimum":0,"maximum":4294967295},"enableLogging":{"type":"boolean"},
             "textNormalization":{"type":"string","enum":["auto","on","off"]},
             "pronunciationDictionaries":{"type":"array","maxItems":3,"items":{"type":"object","required":["pronunciation_dictionary_id"],"properties":{"pronunciation_dictionary_id":{"type":"string","minLength":1},"version_id":{"type":"string"}}}}}
            """)!.AsObject();
        foreach ((string key, JsonNode? value) in common)
        {
            properties[key] = value!.DeepClone();
        }
        if (v3)
        {
            properties["languageCode"] = JsonNode.Parse("""{"type":"string","minLength":2,"maxLength":2}""");
        }
        else
        {
            foreach (string key in new[] { "similarityBoost", "style" })
            {
                properties[key] = JsonNode.Parse("""{"type":"number","minimum":0,"maximum":1}""");
            }
            properties["speakerBoost"] = new JsonObject { ["type"] = "boolean" };
            foreach (string key in new[] { "previousText", "nextText" })
            {
                properties[key] = new JsonObject { ["type"] = "string" };
            }
            foreach (string key in new[] { "previousRequestIds", "nextRequestIds" })
            {
                properties[key] = JsonNode.Parse("""{"type":"array","maxItems":3,"items":{"type":"string"}}""");
            }
        }
        return properties.ToJsonString();
    }
}
