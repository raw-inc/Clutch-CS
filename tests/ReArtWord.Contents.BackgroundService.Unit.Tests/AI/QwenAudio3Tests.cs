using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.AI;

/// <summary>新Qwen-Audioの専用HTTP本文とダウンロード境界を検証します。</summary>
public sealed class QwenAudio3Tests
{
    /// <summary>新APIへ設定を送り、認証を付けず生成URLからWAVまたはMP3を取得します。</summary>
    /// <returns>旧APIの本文と混ざらない生成経路の確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratesAndVerifiesCompleteAudioAsync(bool mp3)
    {
        using MediaTestFiles files = new();
        byte[] bytes = mp3 ? await ElevenLabsSpeechTests.CreateMp3Async(files).ConfigureAwait(true)
                           : await File.ReadAllBytesAsync(await files.CreateAsync("audio").ConfigureAwait(true)).ConfigureAwait(true);
        Uri? root = null;
        AiWireFixture server = new((request, _) => Task.FromResult(request.Method switch
        {
            "GET" => new AiWireResponse(200, "application/octet-stream", bytes),
            _ => AiWireResponse.Json(JsonSerializer.Serialize(new { output = new { finish_reason = "stop", audio = new { url = new Uri(root!, "/audio").AbsoluteUri } } })),
        }));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = server.ConfigureAwait(true);
        root = server.BaseUri;
        Dictionary<string, object> settings = Settings(Path.Combine(files.Root, "generated.audio"));
        settings["outputFormat"] = mp3 ? "mp3"
                                       : "wav";
        settings["speed"] = 0.8;
        settings["pitch"] = 1.2;
        settings["volume"] = 45;
        settings["seed"] = 123;
        settings["languageCode"] = "ja";
        settings["instructions"] = "Calm voice";
        settings["enableSsml"] = false;
        NodeResult result = await ExecuteAsync(server, files, settings).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(bytes, await File.ReadAllBytesAsync((string)settings["outputPath"]).ConfigureAwait(true));
        AiWireRequest creation = Assert.Single(server.Requests, request => request.Method is "POST");
        Assert.Equal("/api/v1/services/audio/tts/SpeechSynthesizer", creation.Path);
        Assert.Equal("Bearer fixture-secret-not-valid", creation.Authorization);
        Assert.Null(Assert.Single(server.Requests, request => request.Method is "GET").Authorization);
        JsonElement body = JsonSerializer.Deserialize<JsonElement>(creation.Body);
        Assert.Equal("qwen-audio-3.0-tts-flash", body.GetProperty("model").GetString());
        JsonElement input = body.GetProperty("input");
        Assert.Equal("longanhuan_v3.6", input.GetProperty("voice").GetString());
        Assert.Equal(24000, input.GetProperty("sample_rate").GetInt32());
        Assert.Equal(settings["outputFormat"], input.GetProperty("format").GetString());
        Assert.Equal(0.8, input.GetProperty("rate").GetDouble());
        Assert.Equal(1.2, input.GetProperty("pitch").GetDouble());
        Assert.Equal(45, input.GetProperty("volume").GetInt32());
        Assert.Equal(123, input.GetProperty("seed").GetInt32());
        Assert.Equal("ja", Assert.Single(input.GetProperty("language_hints").EnumerateArray()).GetString());
        Assert.Equal("Calm voice", input.GetProperty("instruction").GetString());
        Assert.False(input.GetProperty("enable_ssml").GetBoolean());
        Assert.False(input.TryGetProperty("language_type", out _));
    }

    /// <summary>未完了と確定エラーを区別し、自動再生成や生成URLの推測をしません。</summary>
    /// <returns>API呼出しが一回で終わる確認です。</returns>
    [Theory]
    [InlineData("{\"output\":{\"finish_reason\":null}}", EOutcome.Unknown)]
    [InlineData("{\"code\":\"InvalidVoice\"}", EOutcome.Failed)]
    [InlineData("{\"output\":{\"finish_reason\":\"stop\",\"audio\":{\"url\":\"https://unrelated.example/audio\"}}}", EOutcome.Failed)]
    public async Task IncompleteAndRejectedResultsDoNotDownloadAsync(string response, EOutcome expected)
    {
        using MediaTestFiles files = new();
        AiWireFixture server = new((_, _) => Task.FromResult(AiWireResponse.Json(response)));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = server.ConfigureAwait(true);
        NodeResult result = await ExecuteAsync(server, files, Settings(Path.Combine(files.Root, "generated.wav"))).ConfigureAwait(true);
        Assert.Equal(expected, result.Outcome);
        Assert.Single(server.Requests);
        Assert.Empty(Directory.GetFiles(files.Root));
    }

    /// <summary>公式URLはWorkspaceを一つのDNSラベルへ限定して組み立てます。</summary>
    [Theory]
    [InlineData("singapore", "test-workspace", "https://test-workspace.ap-southeast-1.maas.aliyuncs.com/")]
    [InlineData("beijing", "test-workspace", "https://test-workspace.cn-beijing.maas.aliyuncs.com/")]
    [InlineData("beijing", "other.example/", null)]
    [InlineData("beijing", "", null)]
    [InlineData("unknown", "test", null)]
    public void WorkspaceSelectsOnlyTheDeclaredRegion(string region, string workspace, string? expected)
        => Assert.Equal(expected, AiServiceConnection.Endpoint("ai.audio.qwen.audio3.generate", region, workspace)?.AbsoluteUri);

    /// <summary>旧モデル・不正設定・長すぎる入力を送信前に止めます。</summary>
    /// <returns>入力契約を実行時にも検証する確認です。</returns>
    [Fact]
    public async Task InvalidRequestsDoNotDispatchAsync()
    {
        using MediaTestFiles files = new();
        AiWireFixture server = new((_, _) => Task.FromResult(AiWireResponse.Json("{}")));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = server.ConfigureAwait(true);
        foreach ((string key, object value) in new (string, object)[] { ("model", "qwen3-tts-flash"), ("speed", 0.4), ("seed", 65536), ("workspaceId", "invalid/host"), ("sampleRate", 9999) })
        {
            Dictionary<string, object> settings = Settings(Path.Combine(files.Root, "generated.wav"));
            settings[key] = value;
            Assert.Equal(EOutcome.NotExecuted, (await ExecuteAsync(server, files, settings).ConfigureAwait(true)).Outcome);
        }
        Assert.Equal(EOutcome.NotExecuted, (await ExecuteAsync(server, files, Settings(Path.Combine(files.Root, "generated.wav")), new string('a', 20001)).ConfigureAwait(true)).Outcome);
        Assert.Empty(server.Requests);
    }

    /// <summary>存在しないNICや割り当てのないIPv4ではAPIを送信せず、接続設定の問題を明示します。</summary>
    /// <returns>再生成不要な未実行結果と、ファイルに触れていないことの確認です。</returns>
    [Theory]
    [InlineData("interfaceId", "fixture-missing-interface")]
    [InlineData("localAddress", "192.0.2.1")]
    public async Task UnavailableBindingDoesNotDispatchAsync(string key, string value)
    {
        using MediaTestFiles files = new();
        AiWireFixture server = new((_, _) => Task.FromResult(AiWireResponse.Json("{}")));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = server.ConfigureAwait(true);
        Dictionary<string, object> settings = Settings(Path.Combine(files.Root, "generated.wav"));
        settings[key] = value;
        NodeResult result = await ExecuteAsync(server, files, settings).ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, result.Outcome);
        Assert.Equal("ai.networkBindingUnavailable", result.ReasonCode);
        Assert.True(result.FileAccessCompleted);
        Assert.Empty(server.Requests);
        Assert.Empty(Directory.GetFiles(files.Root));
    }

    /// <summary>音声モデルの最小入力を作ります。</summary>
    /// <returns>秘密値を含まない設定です。</returns>
    private static Dictionary<string, object> Settings(string output) => new(StringComparer.Ordinal)
    {
        ["model"] = "qwen-audio-3.0-tts-flash", ["voice"] = "longanhuan_v3.6", ["workspaceId"] = "fixture-workspace", ["outputPath"] = output,
    };

    /// <summary>公開された新Qwenノードを実HTTP fixtureへ接続します。</summary>
    /// <returns>呼出側の観測結果です。</returns>
    private static async Task<NodeResult> ExecuteAsync(AiWireFixture server, MediaTestFiles files, Dictionary<string, object> settings, string prompt = "こんにちは")
        => await AiGeneratorTests.Node(server, files, "ai.audio.qwen.audio3.generate").ExecuteAsync(NodeTestContext.Create(AiGeneratorTests.Settings(server, settings), NodeValue.FromObject(prompt))).ConfigureAwait(true);
}
