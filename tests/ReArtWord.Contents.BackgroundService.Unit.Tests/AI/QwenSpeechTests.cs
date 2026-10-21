using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.AI;

/// <summary>Qwen音声合成の公式HTTP契約、認証分離および生成物の確定境界を確認します。</summary>
public sealed class QwenSpeechTests
{
    /// <summary>音声生成カテゴリに、サービス固有の設定を持つ独立ノードを公開します。</summary>
    [Fact]
    public void AudioCatalogSeparatesProvidersAndPreservesOpenAiContract()
    {
        using MediaTestFiles files = new();
        IReadOnlyList<ReArtWord.Contents.BackgroundService.Runtime.Nodes.INode> nodes = ReArtWord.Contents.BackgroundService.Nodes.AI.AiNodeRegistration.Create(new AiTestSecrets(), new(), files.Options);
        NodeDescriptor openAi = Assert.Single(nodes, node => string.Equals(node.Descriptor.Type, "ai.audio.generate", StringComparison.Ordinal)).Descriptor;
        NodeDescriptor qwen = Assert.Single(nodes, node => string.Equals(node.Descriptor.Type, "ai.audio.qwen.generate", StringComparison.Ordinal)).Descriptor;
        Assert.Equal("AudioGeneration", openAi.Category);
        Assert.Equal(openAi.Category, qwen.Category);
        Assert.Equal("value", Assert.Single(qwen.Outputs).Name);
        Assert.Equal("value", Assert.Single(openAi.Outputs).Name);
        Assert.False(openAi.SettingsSchema.GetProperty("properties").TryGetProperty("region", out _));
        Assert.False(qwen.SettingsSchema.GetProperty("properties").TryGetProperty("provider", out _));
        Assert.Equal("qwen3-tts-vc-2026-01-22", qwen.SettingsSchema.GetProperty("properties").GetProperty("model").GetProperty("default").GetString());
        Assert.True(qwen.SettingsSchema.GetProperty("properties").TryGetProperty("languageType", out _));
    }

    /// <summary>600 Unicode文字を送信し、音声URLを認証なしで取得して実WAVを確定します。</summary>
    /// <returns>既定言語と明示言語の要求・実媒体の検証です。</returns>
    [Theory]
    [InlineData(null, "Auto")]
    [InlineData("Japanese", "Japanese")]
    public async Task CompletedSpeechDownloadsProbedWaveWithoutForwardingSecretAsync(string? language, string expectedLanguage)
    {
        using MediaTestFiles files = new();
        byte[] wave = await File.ReadAllBytesAsync(await files.CreateAsync("audio").ConfigureAwait(true)).ConfigureAwait(true);
        Uri? origin = null;
        AiWireFixture server;
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable serverLifetime = (server = new((request, _) =>
        {
            string artifact = "/speech.wav";
            if (string.Equals(request.Path, artifact, StringComparison.Ordinal))
            {
                return Task.FromResult(new AiWireResponse(200, "audio/wav", wave));
            }
            return Task.FromResult(Completed(new Uri(origin!, "speech.wav").AbsoluteUri));
        })).ConfigureAwait(true);
        origin = server.BaseUri;
        string output = Path.Combine(files.Root, "generated.wav");
        Dictionary<string, object> settings = SpeechSettings(output);
        if (language is not null)
        {
            settings["languageType"] = language;
            settings["region"] = "beijing";
        }
        string prompt = string.Concat(Enumerable.Repeat("😀", 600));
        NodeResult result = await AiGeneratorTests.Node(server, files, "ai.audio.qwen.generate").ExecuteAsync(NodeTestContext.Create(AiGeneratorTests.Settings(server, settings), NodeValue.FromObject(prompt))).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(wave, await File.ReadAllBytesAsync(output).ConfigureAwait(true));
        Assert.Equal("peer", result.Outputs["value"].Json.GetProperty("peerId").GetString());
        Assert.Equal(2, server.Requests.Count);
        AiWireRequest create = Assert.Single(server.Requests, request => string.Equals(request.Method, "POST", StringComparison.Ordinal));
        Assert.Equal("/api/v1/services/aigc/multimodal-generation/generation", create.Path);
        Assert.Equal("Bearer fixture-secret-not-valid", create.Authorization);
        Assert.Null(create.AsyncHeader);
        using JsonDocument body = JsonDocument.Parse(create.Body);
        Assert.Equal("qwen3-tts-vc-2026-01-22", body.RootElement.GetProperty("model").GetString());
        JsonElement input = body.RootElement.GetProperty("input");
        Assert.Equal(prompt, input.GetProperty("text").GetString());
        Assert.Equal("registered-fixture-voice", input.GetProperty("voice").GetString());
        Assert.Equal(expectedLanguage, input.GetProperty("language_type").GetString());
        AiWireRequest download = Assert.Single(server.Requests, request => string.Equals(request.Method, "GET", StringComparison.Ordinal));
        Assert.Equal("/speech.wav", download.Path);
        Assert.Null(download.Authorization);
    }

    /// <summary>サービス提供元、モデル、言語およびregionの不正を送信前に拒否します。</summary>
    /// <returns>APIを呼ばず未実行とする設定境界です。</returns>
    [Theory]
    [InlineData("provider", "unsupported")]
    [InlineData("model", "qwen3-tts-flash")]
    [InlineData("model", "qwen3-tts-vc-realtime-2026-01-15")]
    [InlineData("model", "qwen3-tts-vc-2025-01-01")]
    [InlineData("languageType", "unsupported")]
    [InlineData("region", "unsupported")]
    public async Task UnsupportedSettingsDoNotDispatchAsync(string name, string value)
    {
        using MediaTestFiles files = new();
        AiWireFixture server;
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable serverLifetime = (server = new((_, _) => Task.FromResult(AiWireResponse.Json("{}")))).ConfigureAwait(true);
        Dictionary<string, object> settings = SpeechSettings(Path.Combine(files.Root, "rejected.wav"));
        settings[name] = value;
        NodeResult result = await AiGeneratorTests.Node(server, files, "ai.audio.qwen.generate").ExecuteAsync(NodeTestContext.Create(AiGeneratorTests.Settings(server, settings), NodeValue.FromObject("prompt"))).ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, result.Outcome);
        Assert.Empty(server.Requests);
        Assert.Empty(Directory.GetFiles(files.Root));
    }

    /// <summary>UTF16の長さによらず601 Unicode文字を送信前に拒否します。</summary>
    /// <returns>ASCIIと補助平面文字の上限確認です。</returns>
    [Theory]
    [InlineData("a")]
    [InlineData("😀")]
    public async Task PromptAboveSixHundredRunesDoesNotDispatchAsync(string character)
    {
        using MediaTestFiles files = new();
        AiWireFixture server;
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable serverLifetime = (server = new((_, _) => Task.FromResult(AiWireResponse.Json("{}")))).ConfigureAwait(true);
        string prompt = string.Concat(Enumerable.Repeat(character, 601));
        Dictionary<string, object> settings = SpeechSettings(Path.Combine(files.Root, "rejected.wav"));
        NodeResult result = await AiGeneratorTests.Node(server, files, "ai.audio.qwen.generate").ExecuteAsync(NodeTestContext.Create(AiGeneratorTests.Settings(server, settings), NodeValue.FromObject(prompt))).ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, result.Outcome);
        Assert.Empty(server.Requests);
        Assert.Empty(Directory.GetFiles(files.Root));
    }

    /// <summary>HTTP拒否と終端を確認できない合成応答を区別し、再送・downloadしません。</summary>
    /// <returns>確定失敗と結果不明の区別です。</returns>
    [Theory]
    [InlineData(401, "{\"error\":\"fixture-secret-not-valid\"}", EOutcome.Failed)]
    [InlineData(500, "{\"error\":\"fixture-secret-not-valid\"}", EOutcome.Unknown)]
    [InlineData(200, "{}", EOutcome.Unknown)]
    [InlineData(200, "{\"output\":{\"audio\":{\"url\":\"speech.wav\"}}}", EOutcome.Unknown)]
    [InlineData(200, "{\"output\":{\"finish_reason\":\"unknown\",\"audio\":{\"url\":\"speech.wav\"}}}", EOutcome.Unknown)]
    [InlineData(200, "{\"output\":{\"finish_reason\":\"stop\"}}", EOutcome.Failed)]
    [InlineData(200, "{\"output\":{\"finish_reason\":\"stop\",\"audio\":{\"url\":\"not a URL\"}}}", EOutcome.Failed)]
    public async Task RejectionAndUnfinishedGenerationDoNotDownloadAsync(int status, string response, EOutcome expected)
    {
        using MediaTestFiles files = new();
        AiWireFixture server;
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable serverLifetime = (server = new((_, _) => Task.FromResult(AiWireResponse.Json(response, status)))).ConfigureAwait(true);
        string output = Path.Combine(files.Root, "generated.wav");
        NodeResult result = await AiGeneratorTests.Node(server, files, "ai.audio.qwen.generate").ExecuteAsync(NodeTestContext.Create(AiGeneratorTests.Settings(server, SpeechSettings(output)), NodeValue.FromObject("prompt"))).ConfigureAwait(true);
        Assert.Equal(expected, result.Outcome);
        Assert.Single(server.Requests);
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetFiles(files.Root));
        Assert.DoesNotContain("fixture-secret-not-valid", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    /// <summary>生成完了後のdownload拒否・壊れたWAV・サイズ超過は出力を確定しません。</summary>
    /// <returns>生成済み音声の取得・検証失敗境界です。</returns>
    [Theory]
    [InlineData(404, 1024)]
    [InlineData(200, 1024)]
    [InlineData(200, 1)]
    public async Task ArtifactFailureAfterStopDoesNotCommitOutputAsync(int downloadStatus, int maximumBytes)
    {
        using MediaTestFiles files = new();
        byte[] wave = await File.ReadAllBytesAsync(await files.CreateAsync("audio").ConfigureAwait(true)).ConfigureAwait(true);
        byte[] responseBody = Encoding.UTF8.GetBytes("this is not a valid WAV");
        if (maximumBytes == 1)
        {
            responseBody = wave;
        }
        Uri? origin = null;
        AiWireFixture server;
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable serverLifetime = (server = new((request, _) =>
        {
            string artifact = "/speech.wav";
            if (string.Equals(request.Path, artifact, StringComparison.Ordinal))
            {
                return Task.FromResult(new AiWireResponse(downloadStatus, "audio/wav", responseBody));
            }
            return Task.FromResult(Completed(new Uri(origin!, "speech.wav").AbsoluteUri));
        })).ConfigureAwait(true);
        origin = server.BaseUri;
        string output = Path.Combine(files.Root, "generated.wav");
        Dictionary<string, object> settings = SpeechSettings(output);
        settings["maximumBytes"] = maximumBytes;
        NodeResult result = await AiGeneratorTests.Node(server, files, "ai.audio.qwen.generate").ExecuteAsync(NodeTestContext.Create(AiGeneratorTests.Settings(server, settings), NodeValue.FromObject("prompt"))).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Equal(2, server.Requests.Count);
        Assert.False(File.Exists(output));
        Assert.Single(Directory.GetFiles(files.Root));
        Assert.Null(Assert.Single(server.Requests, request => string.Equals(request.Method, "GET", StringComparison.Ordinal)).Authorization);
    }

    /// <summary>既存出力はAPI送信前に拒否し、内容を保持します。</summary>
    /// <returns>課金要求と上書きを発生させない保護境界です。</returns>
    [Fact]
    public async Task ExistingOutputIsPreservedWithoutDispatchAsync()
    {
        using MediaTestFiles files = new();
        AiWireFixture server;
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable serverLifetime = (server = new((_, _) => Task.FromResult(AiWireResponse.Json("{}")))).ConfigureAwait(true);
        string output = Path.Combine(files.Root, "existing.wav");
        byte[] original = Encoding.UTF8.GetBytes("preserved existing output");
        await File.WriteAllBytesAsync(output, original).ConfigureAwait(true);
        Dictionary<string, object> settings = SpeechSettings(output);
        settings["existingFileMode"] = "skip";
        NodeResult result = await AiGeneratorTests.Node(server, files, "ai.audio.qwen.generate").ExecuteAsync(NodeTestContext.Create(AiGeneratorTests.Settings(server, settings), NodeValue.FromObject("prompt"))).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.True(result.Outputs["value"].Json.GetProperty("skipped").GetBoolean());
        Assert.Empty(server.Requests);
        Assert.Equal(original, await File.ReadAllBytesAsync(output).ConfigureAwait(true));
        Assert.Single(Directory.GetFiles(files.Root));
    }

    /// <summary>試験用の構成のorigin外にある生成物URLへ要求も認証情報も送信しません。</summary>
    /// <returns>別ポートの実サーバーに要求が届かないことの確認です。</returns>
    [Fact]
    public async Task ForeignArtifactOriginIsRejectedWithoutDispatchAsync()
    {
        using MediaTestFiles files = new();
        AiWireFixture foreign;
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable foreignLifetime = (foreign = new((_, _) => Task.FromResult(new AiWireResponse(200, "audio/wav", [])))).ConfigureAwait(true);
        AiWireFixture server;
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable serverLifetime = (server = new((_, _) => Task.FromResult(Completed(new Uri(foreign.BaseUri, "speech.wav").AbsoluteUri)))).ConfigureAwait(true);
        string output = Path.Combine(files.Root, "generated.wav");
        NodeResult result = await AiGeneratorTests.Node(server, files, "ai.audio.qwen.generate").ExecuteAsync(NodeTestContext.Create(AiGeneratorTests.Settings(server, SpeechSettings(output)), NodeValue.FromObject("prompt"))).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Single(server.Requests);
        Assert.Empty(foreign.Requests);
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetFiles(files.Root));
    }

    /// <summary>事前登録voiceを使うQwen設定を作ります。</summary>
    /// <returns>共通NIC・秘密参照設定へ追加する設定です。</returns>
    private static Dictionary<string, object> SpeechSettings(string output)
    {
        return new(StringComparer.Ordinal)
        {
            ["model"] = "qwen3-tts-vc-2026-01-22",
            ["voice"] = "registered-fixture-voice",
            ["outputPath"] = output,
        };
    }

    /// <summary>完了した音声合成のJSON応答を作ります。</summary>
    /// <returns>公式のfinish_reasonとaudio.urlを含む応答です。</returns>
    private static AiWireResponse Completed(string url) => AiWireResponse.Json(JsonSerializer.Serialize(new { output = new { finish_reason = "stop", audio = new { url } } }));
}
