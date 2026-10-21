using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.AI;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.AI;

/// <summary>全生成サービスの保存方針、API送信前のスキップ、既存内容の保全を検証します。</summary>
public sealed class AiOutputPolicyTests
{
    /// <summary>音声・画像・動画の保存先に同じ選択肢を公開し、既存ファイルを再生成しません。</summary>
    /// <returns>各公開ノードを通した正常スキップと後続への参照です。</returns>
    [Theory]
    [InlineData("ai.audio.generate", "gpt-4o-mini-tts")]
    [InlineData("ai.audio.elevenlabs.generate", "eleven_multilingual_v2")]
    [InlineData("ai.audio.elevenlabs.v3.generate", "eleven_v3")]
    [InlineData("ai.audio.elevenlabs.v4.generate", "eleven_v4")]
    [InlineData("ai.audio.elevenlabs.soundEffects", "eleven_text_to_sound_v2")]
    [InlineData("ai.audio.qwen.generate", "qwen3-tts-vc-2026-01-22")]
    [InlineData("ai.audio.qwen.tts.generate", "qwen3-tts-flash")]
    [InlineData("ai.audio.qwen.audio3.generate", "qwen-audio-3.0-tts-flash")]
    [InlineData("ai.picture.generate", "gpt-image-1")]
    [InlineData("ai.video.generate", "wan2.6-t2v")]
    public async Task ExistingFileSkipIsSharedAcrossGeneratorsAsync(string type, string model)
    {
        ArgumentNullException.ThrowIfNull(type);
        using MediaTestFiles files = new();
        AiWireFixture server = new((_, _) => Task.FromResult(AiWireResponse.Json("{}")));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = server.ConfigureAwait(true);
        string output = Path.Combine(files.Root, "existing.audio");
        await File.WriteAllTextAsync(output, "original").ConfigureAwait(true);
        Dictionary<string, object> settings = Settings(type, model, output);
        settings["existingFileMode"] = "skip";
        IActionNode node = AiGeneratorTests.Node(server, files, type);
        NodeResult result = await node.ExecuteAsync(NodeTestContext.Create(AiGeneratorTests.Settings(server, settings), NodeValue.FromObject("hello"))).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Empty(server.Requests);
        NodeValue reference = result.Outputs["value"];
        Assert.Equal("existing", reference.Json.GetProperty("skipReason").GetString());
        Assert.False(reference.Json.TryGetProperty("sha256", out _));
        Assert.Equal(output, await FileArtifactAccess.ResolveInputAsync("peer", reference, new(files.Options.Files)).ConfigureAwait(true));
        Assert.Equal("original", await File.ReadAllTextAsync(output).ConfigureAwait(true));
        JsonElement properties = node.Descriptor.SettingsSchema.GetProperty("properties");
        Assert.Equal("overwrite", properties.GetProperty("existingFileMode").GetProperty("default").GetString());
        Assert.Equal("error", properties.GetProperty("lockedFileMode").GetProperty("default").GetString());
    }

    /// <summary>生成成功時だけ旧ファイルを置き換え、失敗した生成では旧内容を保持します。</summary>
    /// <returns>既定の上書きが検証後に一度だけ行われる確認です。</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DefaultOverwritePreservesOldFileUntilValidatedAsync(bool valid)
    {
        using MediaTestFiles files = new();
        byte[] wave = await File.ReadAllBytesAsync(await files.CreateAsync("audio").ConfigureAwait(true)).ConfigureAwait(true);
        string output = Path.Combine(files.Root, "existing.wav");
        await File.WriteAllTextAsync(output, "original").ConfigureAwait(true);
        AiWireFixture server = new(async (_, token) =>
        {
            Assert.Equal("original", await File.ReadAllTextAsync(output, token).ConfigureAwait(false));
            byte[] response = wave;
            if (!valid)
            {
                response = "broken audio"u8.ToArray();
            }
            return new AiWireResponse(200, "audio/wav", response);
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = server.ConfigureAwait(true);
        NodeResult result = await ExecuteAsync(server, files, Settings("ai.audio.generate", "gpt-4o-mini-tts", output)).ConfigureAwait(true);
        Assert.Single(server.Requests);
        if (valid)
        {
            Assert.Equal(EOutcome.Succeeded, result.Outcome);
            Assert.Equal(wave, await File.ReadAllBytesAsync(output).ConfigureAwait(true));
            Assert.NotEmpty(result.Outputs["value"].Json.GetProperty("sha256").GetString()!);
        }
        else
        {
            Assert.Equal(EOutcome.Failed, result.Outcome);
            Assert.Equal("original", await File.ReadAllTextAsync(output).ConfigureAwait(true));
        }
        Assert.Empty(Directory.GetFiles(files.Root, "*.partial"));
    }

    /// <summary>生成中に作られた同名ファイルにも存在時モードを適用します。</summary>
    /// <returns>保存直前の競合が設定どおり解決される確認です。</returns>
    [Theory]
    [InlineData("skip")]
    [InlineData("overwrite")]
    public async Task LateExistingFileUsesTheSamePolicyAsync(string mode)
    {
        using MediaTestFiles files = new();
        byte[] wave = await File.ReadAllBytesAsync(await files.CreateAsync("audio").ConfigureAwait(true)).ConfigureAwait(true);
        string output = Path.Combine(files.Root, "raced.wav");
        AiWireFixture server = new(async (_, token) =>
        {
            await File.WriteAllTextAsync(output, "late original", token).ConfigureAwait(false);
            return new AiWireResponse(200, "audio/wav", wave);
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = server.ConfigureAwait(true);
        Dictionary<string, object> settings = Settings("ai.audio.generate", "gpt-4o-mini-tts", output);
        settings["existingFileMode"] = mode;
        NodeResult result = await ExecuteAsync(server, files, settings).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        bool skip = mode is "skip";
        if (skip)
        {
            Assert.Equal("late original", await File.ReadAllTextAsync(output).ConfigureAwait(true));
            Assert.True(result.Outputs["value"].Json.GetProperty("skipped").GetBoolean());
        }
        else
        {
            Assert.Equal(wave, await File.ReadAllBytesAsync(output).ConfigureAwait(true));
        }
        Assert.Single(server.Requests);
        Assert.Empty(Directory.GetFiles(files.Root, "*.partial"));
    }

    /// <summary>保存しないテキスト処理には、意味のないファイル設定を表示しません。</summary>
    [Fact]
    public void TextGenerationDoesNotExposeFilePolicies()
    {
        using MediaTestFiles files = new();
        IReadOnlyList<INode> nodes = AiNodeRegistration.Create(new AiTestSecrets(), new(), files.Options);
        foreach (INode node in nodes.Where(item => item.Descriptor.Type is "ai.openai.chat" or "ai.openai.responses" or "ai.text.generate"))
        {
            Assert.False(node.Descriptor.SettingsSchema.GetProperty("properties").TryGetProperty("existingFileMode", out _));
        }
    }

    /// <summary>全生成モデルの必須設定を、試験用途に限定して構築します。</summary>
    /// <returns>出力先とサービス固有の必須設定です。</returns>
    internal static Dictionary<string, object> Settings(string type, string model, string output)
    {
        Dictionary<string, object> settings = new(StringComparer.Ordinal) { ["model"] = model, ["outputPath"] = output };
        bool voice = type.StartsWith("ai.audio.", StringComparison.Ordinal) && type is not "ai.audio.elevenlabs.soundEffects";
        if (voice)
        {
            settings["voice"] = "fixture-voice";
        }
        switch (type)
        {
            case "ai.audio.qwen.audio3.generate": settings["workspaceId"] = "fixture-workspace"; break;
            case "ai.video.generate": settings["duration"] = 2; settings["size"] = "1280*720"; break;
        }
        return settings;
    }

    /// <summary>共有の同期保存経路を実行します。</summary>
    /// <returns>音声生成の結果です。</returns>
    internal static async Task<NodeResult> ExecuteAsync(AiWireFixture server, MediaTestFiles files, Dictionary<string, object> settings)
        => await AiGeneratorTests.Node(server, files, "ai.audio.generate").ExecuteAsync(NodeTestContext.Create(AiGeneratorTests.Settings(server, settings), NodeValue.FromObject("hello"))).ConfigureAwait(true);
}
