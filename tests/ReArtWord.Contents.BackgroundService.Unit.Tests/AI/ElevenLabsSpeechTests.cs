using System.Security.Cryptography;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.AI;
using ReArtWord.Contents.BackgroundService.Nodes.Media;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.AI;

/// <summary>ElevenLabsの両モデル、認証、固定MP3および失敗境界を実HTTPで検証します。</summary>
public sealed class ElevenLabsSpeechTests
{
    /// <summary>独立した音声生成ノードに二つの対応モデルだけを公開します。</summary>
    [Fact]
    public void CatalogExposesTwoModelsAndOnlyTheArtifactOutput()
    {
        using MediaTestFiles files = new();
        NodeDescriptor descriptor = Assert.Single(AiNodeRegistration.Create(new AiTestSecrets(), new(), files.Options), node => string.Equals(node.Descriptor.Type, "ai.audio.elevenlabs.generate", StringComparison.Ordinal)).Descriptor;
        Assert.Equal("AudioGeneration", descriptor.Category);
        Assert.Equal(EExecutionLocation.ExplicitPeer, descriptor.Location);
        Assert.Equal("value", Assert.Single(descriptor.Outputs).Name);
        Assert.Equal("value", Assert.Single(descriptor.Inputs).Name);
        JsonElement properties = descriptor.SettingsSchema.GetProperty("properties");
        Assert.Equal(["eleven_multilingual_v2"], properties.GetProperty("model").GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
        Assert.False(properties.TryGetProperty("provider", out _));
        Assert.False(properties.TryGetProperty("apiKey", out _));
        Assert.False(properties.TryGetProperty("baseUrl", out _));
    }

    /// <summary>各モデルの文字数上限を送り、認証と要求形式・完成MP3を確認します。</summary>
    /// <returns>実HTTPと実ffprobeを通した成功確認です。</returns>
    [Theory]
    [InlineData("eleven_multilingual_v2", 10000)]
    [InlineData("eleven_v3", 5000)]
    public async Task BothModelsProduceVerifiedMp3WithApiKeyAuthenticationAsync(string model, int maximumCharacters)
    {
        using MediaTestFiles files = new();
        byte[] mp3 = await CreateMp3Async(files).ConfigureAwait(true);
        AiWireFixture server;
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable serverLifetime = (server = new((_, _) => Task.FromResult(new AiWireResponse(200, "audio/mpeg", mp3)))).ConfigureAwait(true);
        string output = Path.Combine(files.Root, "generated.mp3");
        string prompt = string.Concat(Enumerable.Repeat("😀", maximumCharacters));
        NodeResult result = await ExecuteAsync(server, files, Settings(output, model), prompt).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(mp3, await File.ReadAllBytesAsync(output).ConfigureAwait(true));
        JsonElement artifact = result.Outputs["value"].Json;
        Assert.Equal("audio/mpeg", artifact.GetProperty("mediaType").GetString());
        Assert.Equal("peer", artifact.GetProperty("peerId").GetString());
        Assert.Equal(output, artifact.GetProperty("path").GetString());
        Assert.Equal(mp3.Length, artifact.GetProperty("size").GetInt64());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(mp3)), artifact.GetProperty("sha256").GetString());
        Assert.Equal("mp3", artifact.GetProperty("probe").GetProperty("format").GetProperty("format_name").GetString());
        AiWireRequest request = Assert.Single(server.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/v1/text-to-speech/fixture_voice-123", request.Path);
        Assert.Equal("?output_format=mp3_44100_128", request.Query);
        Assert.Equal("fixture-secret-not-valid", request.ApiKey);
        Assert.Null(request.Authorization);
        Assert.Null(request.AsyncHeader);
        using JsonDocument body = JsonDocument.Parse(request.Body);
        Assert.Equal(model, body.RootElement.GetProperty("model_id").GetString());
        Assert.Equal(prompt, body.RootElement.GetProperty("text").GetString());
        Assert.Equal(2, body.RootElement.EnumerateObject().Count());
        Assert.Empty(Directory.EnumerateFiles(files.Root, "*.partial"));
    }

    /// <summary>非対応モデルと音声ID、秘密やNIC不足を送信前に拒否します。</summary>
    /// <returns>課金要求がない未実行境界です。</returns>
    [Theory]
    [InlineData("model", "eleven_v3_conversational")]
    [InlineData("model", "eleven_turbo_v2_5")]
    [InlineData("voice", "")]
    [InlineData("voice", "../another-endpoint")]
    [InlineData("voice", "voice?output_format=wav_44100")]
    [InlineData("voice", null)]
    [InlineData("secretRef", "")]
    [InlineData("interfaceId", "nonexistent-fixture-interface")]
    [InlineData("localAddress", "203.0.113.10")]
    public async Task InvalidSettingsDoNotDispatchAsync(string name, string? value)
    {
        using MediaTestFiles files = new();
        AiWireFixture server;
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable serverLifetime = (server = new((_, _) => Task.FromResult(AiWireResponse.Json("{}")))).ConfigureAwait(true);
        Dictionary<string, object> settings = Settings(Path.Combine(files.Root, "generated.mp3"));
        if (value is null)
        {
            settings.Remove(name);
        }
        else
        {
            settings[name] = value;
        }
        NodeResult result = await ExecuteAsync(server, files, settings).ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, result.Outcome);
        Assert.Empty(server.Requests);
        Assert.Empty(Directory.GetFiles(files.Root));
    }

    /// <summary>各モデルの文字数上限超過を一回も送信せず拒否します。</summary>
    /// <returns>サロゲートペアを含むUnicode入力の上限確認です。</returns>
    [Theory]
    [InlineData("eleven_multilingual_v2", 10001)]
    [InlineData("eleven_v3", 5001)]
    public async Task TextAboveModelLimitDoesNotDispatchAsync(string model, int length)
    {
        using MediaTestFiles files = new();
        AiWireFixture server;
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable serverLifetime = (server = new((_, _) => Task.FromResult(AiWireResponse.Json("{}")))).ConfigureAwait(true);
        NodeResult result = await ExecuteAsync(server, files, Settings(Path.Combine(files.Root, "generated.mp3"), model), string.Concat(Enumerable.Repeat("😀", length))).ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, result.Outcome);
        Assert.Empty(server.Requests);
        Assert.Empty(Directory.GetFiles(files.Root));
    }

    /// <summary>HTTP拒否と未確定障害を区別し、秘密をそのまま返さず再生成しません。</summary>
    /// <returns>拒否時Failedと受理不明時Unknownの確認です。</returns>
    [Theory]
    [InlineData(401, EOutcome.Failed)]
    [InlineData(422, EOutcome.Failed)]
    [InlineData(429, EOutcome.Failed)]
    [InlineData(408, EOutcome.Unknown)]
    [InlineData(500, EOutcome.Unknown)]
    public async Task HttpFailuresDoNotRetryOrExposeSecretsAsync(int status, EOutcome expected)
    {
        using MediaTestFiles files = new();
        AiWireFixture server;
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable serverLifetime = (server = new((_, _) => Task.FromResult(AiWireResponse.Json("""{"detail":"fixture-secret-not-valid"}""", status)))).ConfigureAwait(true);
        NodeResult result = await ExecuteAsync(server, files, Settings(Path.Combine(files.Root, "generated.mp3"))).ConfigureAwait(true);
        Assert.Equal(expected, result.Outcome);
        Assert.Single(server.Requests);
        Assert.Empty(Directory.GetFiles(files.Root));
        Assert.DoesNotContain("fixture-secret-not-valid", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    /// <summary>完了した本文がJSONや別媒体ならMP3として確定しません。</summary>
    /// <returns>壊れた出力と実WAVの形式不一致を拒否する確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedInvalidMediaFailsWithoutCommitAsync(bool validWave)
    {
        using MediaTestFiles files = new();
        byte[] bytes = "not an audio file"u8.ToArray();
        if (validWave)
        {
            bytes = await File.ReadAllBytesAsync(await files.CreateAsync("audio").ConfigureAwait(true)).ConfigureAwait(true);
        }
        AiWireFixture server;
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable serverLifetime = (server = new((_, _) => Task.FromResult(new AiWireResponse(200, "audio/mpeg", bytes)))).ConfigureAwait(true);
        string output = Path.Combine(files.Root, "generated.mp3");
        NodeResult result = await ExecuteAsync(server, files, Settings(output)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Single(server.Requests);
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.EnumerateFiles(files.Root, "*.partial"));
    }

    /// <summary>本文転送の上限超過は完了を断定せず、部分ファイルを残しません。</summary>
    /// <returns>有効MP3でもサイズ上限を守る確認です。</returns>
    [Fact]
    public async Task OversizedBodyRemainsUnknownAndDoesNotCommitAsync()
    {
        using MediaTestFiles files = new();
        byte[] mp3 = await CreateMp3Async(files).ConfigureAwait(true);
        AiWireFixture server;
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable serverLifetime = (server = new((_, _) => Task.FromResult(new AiWireResponse(200, "audio/mpeg", mp3)))).ConfigureAwait(true);
        string output = Path.Combine(files.Root, "generated.mp3");
        Dictionary<string, object> settings = Settings(output);
        settings["maximumBytes"] = 1;
        NodeResult result = await ExecuteAsync(server, files, settings).ConfigureAwait(true);
        Assert.Equal(EOutcome.Unknown, result.Outcome);
        Assert.Single(server.Requests);
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.EnumerateFiles(files.Root, "*.partial"));
    }

    /// <summary>既存出力とprobe実行物不足を課金要求の前に拒否します。</summary>
    /// <returns>既存ファイル保持とローカル前提条件の確認です。</returns>
    [Fact]
    public async Task ExistingOutputAndMissingProbeDoNotDispatchAsync()
    {
        using MediaTestFiles files = new();
        AiWireFixture server;
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable serverLifetime = (server = new((_, _) => Task.FromResult(AiWireResponse.Json("{}")))).ConfigureAwait(true);
        string output = Path.Combine(files.Root, "existing.mp3");
        byte[] original = "preserved existing file"u8.ToArray();
        await File.WriteAllBytesAsync(output, original).ConfigureAwait(true);
        Dictionary<string, object> existingSettings = Settings(output);
        existingSettings["existingFileMode"] = "skip";
        NodeResult existing = await ExecuteAsync(server, files, existingSettings).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, existing.Outcome);
        Assert.True(existing.Outputs["value"].Json.GetProperty("skipped").GetBoolean());
        Assert.Equal(original, await File.ReadAllBytesAsync(output).ConfigureAwait(true));
        MediaOptions missing = files.Options with { FfprobePath = Path.Combine(files.Root, "missing-ffprobe.exe") };
        IActionNode node = (IActionNode)Assert.Single(AiNodeRegistration.Create(new AiTestSecrets(), new AiOptions(server.BaseUri), missing), item => string.Equals(item.Descriptor.Type, "ai.audio.elevenlabs.generate", StringComparison.Ordinal));
        NodeResult unavailable = await node.ExecuteAsync(NodeTestContext.Create(AiGeneratorTests.Settings(server, Settings(Path.Combine(files.Root, "generated.mp3"))), NodeValue.FromObject("prompt"))).ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, unavailable.Outcome);
        Assert.Empty(server.Requests);
        Assert.Single(Directory.GetFiles(files.Root));
    }

    /// <summary>転送先へのredirectを追わずAPIキーを外部へ送りません。</summary>
    /// <returns>別originに要求が届かない認証境界です。</returns>
    [Fact]
    public async Task RedirectDoesNotForwardApiKeyAsync()
    {
        using MediaTestFiles files = new();
        AiWireFixture foreign;
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable foreignLifetime = (foreign = new((_, _) => Task.FromResult(AiWireResponse.Json("{}")))).ConfigureAwait(true);
        AiWireFixture server;
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable serverLifetime = (server = new((_, _) => Task.FromResult(AiWireResponse.Json("{}", 307) with { Headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["Location"] = new Uri(foreign.BaseUri, "leak").AbsoluteUri } }))).ConfigureAwait(true);
        NodeResult result = await ExecuteAsync(server, files, Settings(Path.Combine(files.Root, "generated.mp3"))).ConfigureAwait(true);
        Assert.Equal(EOutcome.Unknown, result.Outcome);
        Assert.Single(server.Requests);
        Assert.Empty(foreign.Requests);
        Assert.Empty(Directory.GetFiles(files.Root));
    }

    /// <summary>送信後の期限切れは結果不明として扱い、暗黙に再生成しません。</summary>
    /// <returns>再送なしのタイムアウト境界です。</returns>
    [Fact]
    public async Task TimeoutAfterDispatchRemainsUnknownAsync()
    {
        using MediaTestFiles files = new();
        IterationTestClock clock = new();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
        AiWireFixture server;
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable serverLifetime = (server = new(async (_, token) =>
        {
            clock.Advance(10000);
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return AiWireResponse.Json("{}");
        })).ConfigureAwait(true);
        Dictionary<string, object> settings = Settings(Path.Combine(files.Root, "generated.mp3"));
        NodeResult result = await AiGeneratorTests.Node(server, files, "ai.audio.elevenlabs.generate").ExecuteAsync(
            NodeTestContext.Create(AiGeneratorTests.Settings(server, settings), NodeValue.FromObject("prompt"), time: clock), deadline.Token).ConfigureAwait(true);
        Assert.False(deadline.IsCancellationRequested, "受信後に進めた試験時計で処理の期限を判定できませんでした。");
        Assert.Equal(EOutcome.Unknown, result.Outcome);
        Assert.Single(server.Requests);
        Assert.Empty(Directory.GetFiles(files.Root));
    }

    /// <summary>既存の公開音声Converterで実MP3 試験用の構成を作ります。</summary>
    /// <returns>合成API応答に使う検証済みMP3です。</returns>
    internal static async Task<byte[]> CreateMp3Async(MediaTestFiles files)
    {
        string input = await files.CreateAsync("audio").ConfigureAwait(true);
        string output = Path.Combine(files.Root, "fixture.mp3");
        IActionNode converter = (IActionNode)Assert.Single(MediaNodeRegistration.Create(files.Options), node => string.Equals(node.Descriptor.Type, "media.audio.convert", StringComparison.Ordinal));
        NodeResult result = await converter.ExecuteAsync(NodeTestContext.Create(JsonSerializer.Serialize(new { outputPath = output, format = "mp3", sampleRate = 44100 }), NodeValue.FromObject(input))).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        return await File.ReadAllBytesAsync(output).ConfigureAwait(true);
    }

    /// <summary>ElevenLabs専用設定を共通通信設定へ追加します。</summary>
    /// <returns>秘密の実値を含まない設定です。</returns>
    private static Dictionary<string, object> Settings(string output, string model = "eleven_multilingual_v2") => new(StringComparer.Ordinal)
    {
        ["model"] = model,
        ["voice"] = "fixture_voice-123",
        ["outputPath"] = output,
    };

    /// <summary>公開生成処理のElevenLabsノードを実行します。</summary>
    /// <returns>利用者が観測する実行結果です。</returns>
    private static async Task<NodeResult> ExecuteAsync(AiWireFixture server, MediaTestFiles files, Dictionary<string, object> settings, string prompt = "読み上げるテキスト")
    {
        string type = settings.GetValueOrDefault("model")?.ToString() switch
        {
            "eleven_v3" => "ai.audio.elevenlabs.v3.generate",
            _ => "ai.audio.elevenlabs.generate",
        };
        return await AiGeneratorTests.Node(server, files, type).ExecuteAsync(NodeTestContext.Create(AiGeneratorTests.Settings(server, settings), NodeValue.FromObject(prompt))).ConfigureAwait(true);
    }
}
