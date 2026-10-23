using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.Media;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.AI;

/// <summary>ElevenLabsのWAV出力と小数設定を実HTTP・実媒体で検証します。</summary>
public sealed class ElevenLabsAudioFormatTests(ITestOutputHelper testOutput)
{
    /// <summary>両TTSモデルの各WAV形式を要求し、受信した媒体を変更せず保存します。</summary>
    /// <returns>要求形式、保存内容、MIME種別とprobeの確認です。</returns>
    [Theory]
    [InlineData(false, 8000)]
    [InlineData(false, 16000)]
    [InlineData(false, 22050)]
    [InlineData(false, 24000)]
    [InlineData(false, 32000)]
    [InlineData(false, 44100)]
    [InlineData(false, 48000)]
    [InlineData(true, 8000)]
    [InlineData(true, 16000)]
    [InlineData(true, 22050)]
    [InlineData(true, 24000)]
    [InlineData(true, 32000)]
    [InlineData(true, 44100)]
    [InlineData(true, 48000)]
    public async Task SpeechSavesSelectedWaveWithoutTranscodingAsync(bool v3, int sampleRate)
    {
        using MediaTestFiles files = new(testOutput);
        byte[] wave = await CreateWaveAsync(files, sampleRate).ConfigureAwait(true);
        AiWireFixture server;
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = (server = new((_, _) => Task.FromResult(new AiWireResponse(200, "audio/wav", wave)))).ConfigureAwait(true);
        string output = Path.Combine(files.Root, "generated.wav");
        string format = "wav_" + sampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Dictionary<string, object> settings = new(StringComparer.Ordinal) { ["voice"] = "voice", ["outputPath"] = output, ["outputFormat"] = format, ["stability"] = 0.35 };
        NodeResult result = await ExecuteAsync(server, files, v3, settings).ConfigureAwait(true);
        Assert.True(result.Outcome == EOutcome.Succeeded, result.ReasonCode + ": " + result.Message);
        Assert.Equal(wave, await File.ReadAllBytesAsync(output).ConfigureAwait(true));
        JsonElement artifact = result.Outputs["value"].Json;
        Assert.Equal("audio/wav", artifact.GetProperty("mediaType").GetString());
        Assert.Equal(output, artifact.GetProperty("path").GetString());
        Assert.Equal("wav", artifact.GetProperty("probe").GetProperty("format").GetProperty("format_name").GetString());
        JsonElement stream = Assert.Single(artifact.GetProperty("probe").GetProperty("streams").EnumerateArray());
        Assert.Equal(sampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture), stream.GetProperty("sample_rate").GetString());
        AiWireRequest request = Assert.Single(server.Requests);
        Assert.Equal("?output_format=" + format, request.Query);
        Assert.Equal(0.35, JsonSerializer.Deserialize<JsonElement>(request.Body).GetProperty("voice_settings").GetProperty("stability").GetDouble());
        Assert.Empty(Directory.EnumerateFiles(files.Root, "*.partial"));
    }

    /// <summary>WAVを要求してMP3が届いた場合は生成物として確定しません。</summary>
    /// <returns>偽装されたMIMEを信頼せず、部分ファイルも取り除く確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaveRequestRejectsMp3PayloadAsync(bool v3)
    {
        using MediaTestFiles files = new(testOutput);
        byte[] mp3 = await ElevenLabsSpeechTests.CreateMp3Async(files).ConfigureAwait(true);
        AiWireFixture server;
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = (server = new((_, _) => Task.FromResult(new AiWireResponse(200, "audio/wav", mp3)))).ConfigureAwait(true);
        string output = Path.Combine(files.Root, "generated.wav");
        Dictionary<string, object> settings = new(StringComparer.Ordinal) { ["voice"] = "voice", ["outputPath"] = output, ["outputFormat"] = "wav_44100" };
        NodeResult result = await ExecuteAsync(server, files, v3, settings).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Single(server.Requests);
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.EnumerateFiles(files.Root, "*.partial"));
    }

    /// <summary>小数を許可しても安定度の範囲外は要求前に拒否します。</summary>
    /// <returns>無効な入力でAPIを呼び出さない確認です。</returns>
    [Theory]
    [InlineData(false, -0.01)]
    [InlineData(false, 1.01)]
    [InlineData(true, -0.01)]
    [InlineData(true, 1.01)]
    public async Task StabilityOutsideRangeDoesNotDispatchAsync(bool v3, double stability)
    {
        using MediaTestFiles files = new(testOutput);
        AiWireFixture server;
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = (server = new((_, _) => Task.FromResult(AiWireResponse.Json("{}")))).ConfigureAwait(true);
        Dictionary<string, object> settings = new(StringComparer.Ordinal) { ["voice"] = "voice", ["outputPath"] = Path.Combine(files.Root, "generated.wav"), ["outputFormat"] = "wav_24000", ["stability"] = stability };
        NodeResult result = await ExecuteAsync(server, files, v3, settings).ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, result.Outcome);
        Assert.Empty(server.Requests);
        Assert.Empty(Directory.GetFiles(files.Root));
    }

    /// <summary>APIごとの出力形式を混同して効果音へWAVを送らないことを確認します。</summary>
    /// <returns>効果音ノードの未対応形式を送信前に拒否する確認です。</returns>
    [Fact]
    public async Task SoundEffectsDoNotAdvertiseTtsOnlyWaveFormatsAsync()
    {
        using MediaTestFiles files = new(testOutput);
        AiWireFixture server;
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = (server = new((_, _) => Task.FromResult(AiWireResponse.Json("{}")))).ConfigureAwait(true);
        Dictionary<string, object> settings = new(StringComparer.Ordinal) { ["model"] = "eleven_text_to_sound_v2", ["outputPath"] = Path.Combine(files.Root, "generated.wav"), ["outputFormat"] = "wav_24000" };
        NodeResult result = await AiGeneratorTests.Node(server, files, "ai.audio.elevenlabs.soundEffects").ExecuteAsync(NodeTestContext.Create(AiGeneratorTests.Settings(server, settings), NodeValue.FromObject("rain"))).ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, result.Outcome);
        Assert.Empty(server.Requests);
    }

    /// <summary>公開変換ノードで要求したサンプルレートの実WAVを作ります。</summary>
    /// <returns>応答本文に使う無圧縮音声です。</returns>
    private static async Task<byte[]> CreateWaveAsync(MediaTestFiles files, int sampleRate)
    {
        string input = await files.CreateAsync("audio").ConfigureAwait(true);
        string output = Path.Combine(files.Root, "fixture.wav");
        IActionNode converter = (IActionNode)Assert.Single(MediaNodeRegistration.Create(files.Options), node => string.Equals(node.Descriptor.Type, "media.audio.convert", StringComparison.Ordinal));
        NodeResult result = await converter.ExecuteAsync(NodeTestContext.Create(JsonSerializer.Serialize(new { outputPath = output, format = "wav", sampleRate, channels = 1 }), NodeValue.FromObject(input))).ConfigureAwait(true);
        Assert.True(result.Outcome == EOutcome.Succeeded, result.ReasonCode + ": " + result.Message);
        return await File.ReadAllBytesAsync(output).ConfigureAwait(true);
    }

    /// <summary>既定モデルを含む公開ノード設定を通して音声を生成します。</summary>
    /// <returns>設定検証とHTTP要求を経た実行結果です。</returns>
    private static ValueTask<NodeResult> ExecuteAsync(AiWireFixture server, MediaTestFiles files, bool v3, Dictionary<string, object> settings)
    {
        string type = "ai.audio.elevenlabs.generate";
        if (v3)
        {
            type = "ai.audio.elevenlabs.v3.generate";
        }
        IActionNode node = AiGeneratorTests.Node(server, files, type);
        settings["model"] = node.Descriptor.SettingsSchema.GetProperty("properties").GetProperty("model").GetProperty("default").GetString()!;
        return node.ExecuteAsync(NodeTestContext.Create(AiGeneratorTests.Settings(server, settings), NodeValue.FromObject("test")));
    }
}
