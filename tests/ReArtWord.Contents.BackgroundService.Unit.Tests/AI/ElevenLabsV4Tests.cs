using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.AI;

/// <summary>v4専用のDialogue要求・任意設定・型契約を実HTTPと実媒体で検証します。</summary>
public sealed class ElevenLabsV4Tests
{
    /// <summary>単一話者をinputsへ包み、未指定設定を追加せずMP3またはWAVを確定します。</summary>
    /// <returns>API要求と検証済み音声の確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratesOneVoiceWithDialogueApiAsync(bool wave)
    {
        using MediaTestFiles files = new();
        byte[] bytes = wave ? await File.ReadAllBytesAsync(await files.CreateAsync("audio").ConfigureAwait(true)).ConfigureAwait(true)
                            : await ElevenLabsSpeechTests.CreateMp3Async(files).ConfigureAwait(true);
        string format = wave ? "wav_16000"
                             : "mp3_44100_128";
        string mime = wave ? "audio/wav"
                           : "audio/mpeg";
        AiWireFixture server = new((_, _) => Task.FromResult(new AiWireResponse(200, mime, bytes)));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = server.ConfigureAwait(true);
        string extension = wave ? ".wav"
                                : ".mp3";
        string output = Path.Combine(files.Root, "v4" + extension);
        Dictionary<string, object> settings = Settings(output);
        settings["outputFormat"] = format;
        string prompt = string.Concat(Enumerable.Repeat("😀", 10000));
        NodeResult result = await ExecuteAsync(server, files, settings, prompt).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(output).ConfigureAwait(true));
        Assert.Equal(mime, result.Outputs["value"].Json.GetProperty("mediaType").GetString());
        AiWireRequest request = Assert.Single(server.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/v1/text-to-dialogue", request.Path);
        Assert.Equal("?output_format=" + format, request.Query);
        Assert.Equal("fixture-secret-not-valid", request.ApiKey);
        Assert.Null(request.Authorization);
        JsonElement body = JsonSerializer.Deserialize<JsonElement>(request.Body);
        Assert.Equal("eleven_v4", body.GetProperty("model_id").GetString());
        Assert.Equal(2, body.EnumerateObject().Count());
        JsonElement input = Assert.Single(body.GetProperty("inputs").EnumerateArray());
        Assert.Equal(prompt, input.GetProperty("text").GetString());
        Assert.Equal("fixture_voice-123", input.GetProperty("voice_id").GetString());
    }

    /// <summary>連続値の安定度・類似度と前後文をDialogueの正しい項目へ渡します。</summary>
    /// <returns>旧TTSのvoice_settingsやspeedを送らない確認です。</returns>
    [Fact]
    public async Task SendsOnlyConfiguredDialogueSettingsAsync()
    {
        using MediaTestFiles files = new();
        byte[] bytes = await ElevenLabsSpeechTests.CreateMp3Async(files).ConfigureAwait(true);
        AiWireFixture server = new((_, _) => Task.FromResult(new AiWireResponse(200, "audio/mpeg", bytes)));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = server.ConfigureAwait(true);
        Dictionary<string, object> settings = Settings(Path.Combine(files.Root, "v4.mp3"));
        settings["stability"] = 0.35;
        settings["similarity"] = 0.825;
        settings["previousText"] = "before";
        settings["nextText"] = "after";
        settings["previousRequestIds"] = new[] { "previous" };
        settings["nextRequestIds"] = new[] { "next" };
        settings["seed"] = 4294967295L;
        settings["enableLogging"] = false;
        settings["languageCode"] = "ja";
        settings["usePvcAsIvc"] = true;
        NodeResult result = await ExecuteAsync(server, files, settings, "[whispering] こんにちは").ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        AiWireRequest request = Assert.Single(server.Requests);
        JsonElement body = JsonSerializer.Deserialize<JsonElement>(request.Body);
        Assert.Equal(0.35, body.GetProperty("settings").GetProperty("stability").GetDouble());
        Assert.Equal(0.825, body.GetProperty("settings").GetProperty("similarity").GetDouble());
        Assert.Equal("before", body.GetProperty("previous_text").GetString());
        Assert.Equal("after", body.GetProperty("future_text").GetString());
        Assert.Equal("previous", body.GetProperty("previous_request_ids")[0].GetString());
        Assert.Equal("next", body.GetProperty("next_request_ids")[0].GetString());
        Assert.Equal(4294967295L, body.GetProperty("seed").GetInt64());
        Assert.Equal("ja", body.GetProperty("language_code").GetString());
        Assert.True(body.GetProperty("use_pvc_as_ivc").GetBoolean());
        Assert.False(body.TryGetProperty("voice_settings", out _));
        Assert.Contains("enable_logging=false", request.Query, StringComparison.Ordinal);
    }

    /// <summary>非対応の項目・モデル・上限を生成要求の前に拒否します。</summary>
    /// <returns>誤った設定で外部要求を送らない確認です。</returns>
    [Theory]
    [InlineData("speed", "1.0")]
    [InlineData("style", "0.5")]
    [InlineData("similarity", "1.01")]
    [InlineData("stability", "-0.01")]
    [InlineData("model", "\"eleven_v4_turbo\"")]
    [InlineData("voice", "\"../voice\"")]
    public async Task InvalidSettingsDoNotDispatchAsync(string key, string json)
    {
        using MediaTestFiles files = new();
        AiWireFixture server = new((_, _) => Task.FromResult(AiWireResponse.Json("{}")));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = server.ConfigureAwait(true);
        Dictionary<string, object> settings = Settings(Path.Combine(files.Root, "v4.mp3"));
        settings[key] = JsonSerializer.Deserialize<JsonElement>(json);
        Assert.Equal(EOutcome.NotExecuted, (await ExecuteAsync(server, files, settings, "hello").ConfigureAwait(true)).Outcome);
        Assert.Empty(server.Requests);
    }

    /// <summary>モデルと前後文の上限を守り、カタログの接続先・入出力型も公開します。</summary>
    /// <returns>文字数とGUIへ渡す契約の確認です。</returns>
    [Fact]
    public async Task LimitsAndCatalogMatchTheNewModelAsync()
    {
        using MediaTestFiles files = new();
        AiWireFixture server = new((_, _) => Task.FromResult(AiWireResponse.Json("{}")));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = server.ConfigureAwait(true);
        Dictionary<string, object> settings = Settings(Path.Combine(files.Root, "v4.mp3"));
        Assert.Equal(EOutcome.NotExecuted, (await ExecuteAsync(server, files, settings, new string('a', 10001)).ConfigureAwait(true)).Outcome);
        settings["previousText"] = new string('a', 101);
        Assert.Equal(EOutcome.NotExecuted, (await ExecuteAsync(server, files, settings, "hello").ConfigureAwait(true)).Outcome);
        Assert.Empty(server.Requests);
        string type = "ai.audio.elevenlabs.v4.generate";
        NodeDescriptor descriptor = AiGeneratorTests.Node(server, files, type).Descriptor;
        Assert.Equal("AudioGeneration", descriptor.Category);
        Assert.Equal("value", Assert.Single(descriptor.Outputs).Name);
        Assert.Equal("https://api.elevenlabs.io/", AiServiceConnection.Endpoint(type)!.AbsoluteUri);
        Assert.Equal("string", BuiltinInputTypes.Expected(type, "value")!.Type);
        Assert.Equal("object", BuiltinOutputTypes.Get(type, "value")!.Type);
    }

    /// <summary>共通通信設定にv4の最小設定を追加します。</summary>
    /// <returns>任意設定を含まない生成設定です。</returns>
    private static Dictionary<string, object> Settings(string output) => new(StringComparer.Ordinal)
    {
        ["model"] = "eleven_v4", ["voice"] = "fixture_voice-123", ["outputPath"] = output,
    };

    /// <summary>公開ノードの実行境界からfixtureへ一度だけ要求します。</summary>
    /// <returns>呼出側が観測するノード結果です。</returns>
    private static async Task<NodeResult> ExecuteAsync(AiWireFixture server, MediaTestFiles files, Dictionary<string, object> settings, string prompt)
        => await AiGeneratorTests.Node(server, files, "ai.audio.elevenlabs.v4.generate").ExecuteAsync(NodeTestContext.Create(AiGeneratorTests.Settings(server, settings), NodeValue.FromObject(prompt))).ConfigureAwait(true);
}
