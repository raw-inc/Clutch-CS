using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.AI;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.AI;

/// <summary>音声レシピのモデル分離、任意項目、接続先指定を実HTTPで検証します。</summary>
public sealed class AudioRecipeTests
{
    /// <summary>効果音の任意項目は未指定時に送らず、指定時にはAPI名へ変換します。</summary>
    /// <returns>完成MP3と送信契約の確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SoundEffectsUseOptionalParametersAsync(bool configured)
    {
        using MediaTestFiles files = new();
        byte[] audio = await ElevenLabsSpeechTests.CreateMp3Async(files).ConfigureAwait(true);
        AiWireFixture server;
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = (server = new((_, _) => Task.FromResult(new AiWireResponse(200, "audio/mpeg", audio)))).ConfigureAwait(true);
        JsonObject settings = JsonNode.Parse(AiGeneratorTests.Settings(server))!.AsObject();
        settings.Remove("model");
        settings["outputPath"] = Path.Combine(files.Root, "sound.mp3");
        if (configured)
        {
            settings["durationSeconds"] = 0.5;
            settings["promptInfluence"] = 0.7;
            settings["loop"] = true;
        }
        NodeResult result = await AiGeneratorTests.Node(server, files, "ai.audio.elevenlabs.soundEffects").ExecuteAsync(NodeTestContext.Create(settings.ToJsonString(), NodeValue.FromObject("rain"))).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        AiWireRequest request = Assert.Single(server.Requests);
        Assert.Equal("/v1/sound-generation", request.Path);
        Assert.Equal("fixture-secret-not-valid", request.ApiKey);
        Assert.Null(request.Authorization);
        JsonObject body = JsonNode.Parse(request.Body)!.AsObject();
        Assert.Equal("rain", body["text"]!.GetValue<string>());
        Assert.Equal(configured switch { true => 4, false => 1 }, body.Count);
        if (configured)
        {
            Assert.Equal(0.5, body["duration_seconds"]!.GetValue<double>());
            Assert.Equal(0.7, body["prompt_influence"]!.GetValue<double>());
            Assert.True(body["loop"]!.GetValue<bool>());
        }
    }

    /// <summary>モデル固有設定を誤ったレシピへ送らず、指定済みvoice settingsをネストします。</summary>
    /// <returns>v3の安定度と任意seedの送信です。</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(0.35)]
    [InlineData(0.375)]
    [InlineData(0.5)]
    [InlineData(1)]
    public async Task V3HasIndependentContractAndNestedVoiceSettingsAsync(double stability)
    {
        using MediaTestFiles files = new();
        byte[] audio = await ElevenLabsSpeechTests.CreateMp3Async(files).ConfigureAwait(true);
        AiWireFixture server;
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = (server = new((_, _) => Task.FromResult(new AiWireResponse(200, "audio/mpeg", audio)))).ConfigureAwait(true);
        Dictionary<string, object> settings = new(StringComparer.Ordinal) { ["model"] = "eleven_v3", ["voice"] = "voice", ["outputPath"] = Path.Combine(files.Root, "v3.mp3"), ["stability"] = stability, ["speed"] = 1.1, ["seed"] = 4294967295L, ["enableLogging"] = false };
        IActionNode node = AiGeneratorTests.Node(server, files, "ai.audio.elevenlabs.v3.generate");
        NodeResult result = await node.ExecuteAsync(NodeTestContext.Create(AiGeneratorTests.Settings(server, settings), NodeValue.FromObject("hello"))).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        AiWireRequest request = Assert.Single(server.Requests);
        Assert.Contains("enable_logging=false", request.Query, StringComparison.Ordinal);
        JsonElement body = JsonSerializer.Deserialize<JsonElement>(request.Body);
        Assert.Equal(stability, body.GetProperty("voice_settings").GetProperty("stability").GetDouble());
        Assert.Equal(1.1, body.GetProperty("voice_settings").GetProperty("speed").GetDouble());
        Assert.Equal(4294967295L, body.GetProperty("seed").GetInt64());
        settings["stability"] = 1.01;
        Assert.Equal(EOutcome.NotExecuted, (await node.ExecuteAsync(NodeTestContext.Create(AiGeneratorTests.Settings(server, settings), NodeValue.FromObject("hello"))).ConfigureAwait(true)).Outcome);
        Assert.Single(server.Requests);
        NodeDescriptor descriptor = node.Descriptor;
        Assert.Equal("eleven_v3", Assert.Single(descriptor.SettingsSchema.GetProperty("properties").GetProperty("model").GetProperty("enum").EnumerateArray()).GetString());
        Assert.False(descriptor.SettingsSchema.GetProperty("properties").TryGetProperty("speakerBoost", out _));
    }

    /// <summary>指定URLをそのまま使い、不要なリージョンを読みません。</summary>
    /// <returns>Qwenの通常モデルと音声URLの認証分離です。</returns>
    [Fact]
    public async Task QwenEndpointOverridesRegionWithoutChangingProtocolAsync()
    {
        using MediaTestFiles files = new();
        byte[] wave = await File.ReadAllBytesAsync(await files.CreateAsync("audio").ConfigureAwait(true)).ConfigureAwait(true);
        Uri? endpoint = null;
        AiWireFixture server;
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = (server = new((request, _) => Task.FromResult(request.Method switch
        {
            "GET" => new AiWireResponse(200, "audio/wav", wave),
            _ => AiWireResponse.Json(JsonSerializer.Serialize(new { output = new { finish_reason = "stop", audio = new { url = new Uri(endpoint!, "/audio.wav").AbsoluteUri } } })),
        }))).ConfigureAwait(true);
        endpoint = new(server.BaseUri, "custom/generate?deployment=tts");
        string settings = AiGeneratorTests.Settings(server, new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["model"] = "qwen3-tts-flash", ["voice"] = "Cherry", ["endpoint"] = endpoint.AbsoluteUri, ["region"] = "unused", ["languageType"] = "Japanese", ["outputPath"] = Path.Combine(files.Root, "qwen.wav"),
        });
        NodeResult result = await AiGeneratorTests.Node(server, files, "ai.audio.qwen.tts.generate").ExecuteAsync(NodeTestContext.Create(settings, NodeValue.FromObject("こんにちは"))).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        AiWireRequest creation = Assert.Single(server.Requests, request => string.Equals(request.Method, "POST", StringComparison.Ordinal));
        Assert.Equal("/custom/generate", creation.Path);
        Assert.Equal("?deployment=tts", creation.Query);
        JsonElement body = JsonSerializer.Deserialize<JsonElement>(creation.Body);
        Assert.Equal("Japanese", body.GetProperty("input").GetProperty("language_type").GetString());
        Assert.Equal("Cherry", body.GetProperty("input").GetProperty("voice").GetString());
        Assert.Null(Assert.Single(server.Requests, request => string.Equals(request.Method, "GET", StringComparison.Ordinal)).Authorization);
    }

    /// <summary>認証不要の互換Speech APIにも指定URLとオプションを渡せます。</summary>
    /// <returns>実WAVの検証とbodyの確認です。</returns>
    [Fact]
    public async Task OpenAiSpeechSupportsEndpointInstructionsAndSpeedAsync()
    {
        using MediaTestFiles files = new();
        byte[] wave = await File.ReadAllBytesAsync(await files.CreateAsync("audio").ConfigureAwait(true)).ConfigureAwait(true);
        AiWireFixture server;
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = (server = new((_, _) => Task.FromResult(new AiWireResponse(200, "audio/wav", wave)))).ConfigureAwait(true);
        JsonObject settings = JsonNode.Parse(AiGeneratorTests.Settings(server))!.AsObject();
        settings.Remove("secretRef");
        settings["endpoint"] = new Uri(server.BaseUri, "speech").AbsoluteUri;
        settings["model"] = "gpt-4o-mini-tts";
        settings["voice"] = "coral";
        settings["instructions"] = "Speak calmly";
        settings["speed"] = 0.8;
        settings["outputPath"] = Path.Combine(files.Root, "openai.wav");
        NodeResult result = await AiGeneratorTests.Node(server, files, "ai.audio.generate").ExecuteAsync(NodeTestContext.Create(settings.ToJsonString(), NodeValue.FromObject("hello"))).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        AiWireRequest request = Assert.Single(server.Requests);
        Assert.Equal("/speech", request.Path);
        Assert.Null(request.Authorization);
        JsonElement body = JsonSerializer.Deserialize<JsonElement>(request.Body);
        Assert.Equal("Speak calmly", body.GetProperty("instructions").GetString());
        Assert.Equal(0.8, body.GetProperty("speed").GetDouble());
    }
}
