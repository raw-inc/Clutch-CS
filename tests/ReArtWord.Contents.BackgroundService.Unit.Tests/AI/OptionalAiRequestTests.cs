using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.AI;

/// <summary>設定へ直接渡したnullも省き、各生成APIの実HTTP本文でゼロ・falseを保持します。</summary>
public sealed class OptionalAiRequestTests
{
    /// <summary>ノード検証を通して送信し、サービス別の名前変換後にも省略を確認します。</summary>
    /// <returns>外部APIを使わない三種類の要求本文です。</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task NullSettingsAreOmittedBeforeProviderValidationAndDispatchAsync(int provider)
    {
        using MediaTestFiles files = new();
        AiWireFixture server = new((_, _) => Task.FromResult(AiWireResponse.Json("{}", 400)));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = server.ConfigureAwait(true);
        (string Type, Dictionary<string, object> Settings) selected = provider switch
        {
            0 => ("ai.audio.qwen.audio3.generate", new()
            {
                ["model"] = "qwen-audio-3.0-tts-flash", ["voice"] = "longanhuan_v3.6", ["workspaceId"] = "fixture-workspace",
                ["outputPath"] = Path.Combine(files.Root, "unused.wav"), ["instructions"] = null!, ["pitch"] = null!, ["speed"] = null!,
                ["volume"] = 0, ["seed"] = 0, ["enableSsml"] = false,
            }),
            1 => ("ai.audio.elevenlabs.generate", new()
            {
                ["model"] = "eleven_multilingual_v2", ["voice"] = "fixture_voice", ["outputPath"] = Path.Combine(files.Root, "unused.mp3"),
                ["previousText"] = null!, ["nextText"] = null!, ["speed"] = null!, ["enableLogging"] = null!, ["stability"] = 0, ["seed"] = 0, ["speakerBoost"] = false,
            }),
            _ => ("ai.openai.responses", new()
            {
                ["instructions"] = null!, ["temperature"] = null!, ["conversation"] = null!, ["previousResponseId"] = null!, ["topP"] = 0, ["store"] = false,
            }),
        };
        NodeResult result = await AiGeneratorTests.Node(server, files, selected.Type).ExecuteAsync(NodeTestContext.Create(AiGeneratorTests.Settings(server, selected.Settings), NodeValue.FromObject("test"))).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, result.Outcome);
        AiWireRequest request = Assert.Single(server.Requests);
        JsonElement body = JsonSerializer.Deserialize<JsonElement>(request.Body);
        if (provider == 0)
        {
            JsonElement input = body.GetProperty("input");
            Assert.False(input.TryGetProperty("instruction", out _));
            Assert.False(input.TryGetProperty("pitch", out _));
            Assert.False(input.TryGetProperty("rate", out _));
            Assert.Equal(0, input.GetProperty("volume").GetInt32());
            Assert.Equal(0, input.GetProperty("seed").GetInt32());
            Assert.False(input.GetProperty("enable_ssml").GetBoolean());
        }
        if (provider == 1)
        {
            Assert.False(body.TryGetProperty("previous_text", out _));
            Assert.False(body.TryGetProperty("next_text", out _));
            Assert.Equal(0, body.GetProperty("seed").GetInt32());
            JsonElement voice = body.GetProperty("voice_settings");
            Assert.False(voice.TryGetProperty("speed", out _));
            Assert.Equal(0, voice.GetProperty("stability").GetInt32());
            Assert.False(voice.GetProperty("use_speaker_boost").GetBoolean());
            Assert.DoesNotContain("enable_logging", request.Query, StringComparison.Ordinal);
        }
        if (1 < provider)
        {
            foreach (string key in new[] { "instructions", "temperature", "conversation", "previous_response_id" })
            {
                Assert.False(body.TryGetProperty(key, out _));
            }
            Assert.Equal(0, body.GetProperty("top_p").GetInt32());
            Assert.False(body.GetProperty("store").GetBoolean());
        }
        Assert.Empty(Directory.GetFiles(files.Root));
    }
}
