using System.Security.Cryptography;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.Media;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Media;

/// <summary>実変換・出力検証・許可境界を観測します。</summary>
public sealed class MediaConverterTests(ITestOutputHelper testOutput)
{
    /// <summary>三媒体を実変換し、実ファイルのハッシュとストリームを照合します。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Theory]
    [InlineData("audio", "flac", "audio")]
    [InlineData("video", "mp4", "video")]
    [InlineData("picture", "png", "video")]
    public async Task ConversionProducesVerifiedArtifact(string kind, string format, string expectedStream)
    {
        using MediaTestFiles files = new(testOutput);
        string input = await files.CreateAsync(kind).ConfigureAwait(true);
        string output = Path.Combine(files.Root, "converted." + format);
        IActionNode node = (IActionNode)Assert.Single(MediaNodeRegistration.Create(files.Options), item => string.Equals(item.Descriptor.Type, "media." + kind + ".convert", StringComparison.Ordinal));
        NodeResult result = await node.ExecuteAsync(NodeTestContext.Create(JsonSerializer.Serialize(new { outputPath = output, format }), NodeValue.FromObject(input)));
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(EExecutionLocation.ExplicitPeer, node.Descriptor.Location);
        JsonElement value = result.Outputs["value"].Json;
        Assert.Equal(output, value.GetProperty("path").GetString());
        Assert.Equal(new FileInfo(output).Length, value.GetProperty("size").GetInt64());
        byte[] bytes = await File.ReadAllBytesAsync(output).ConfigureAwait(true);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), value.GetProperty("sha256").GetString());
        Assert.Contains(value.GetProperty("probe").GetProperty("streams").EnumerateArray(), stream => string.Equals(stream.GetProperty("codec_type").GetString(), expectedStream, StringComparison.Ordinal));
        NodeResult conflict = await node.ExecuteAsync(NodeTestContext.Create(JsonSerializer.Serialize(new { outputPath = output, format, existingFileMode = "error" }), NodeValue.FromObject(input)));
        Assert.Equal(EOutcome.NotExecuted, conflict.Outcome);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(output).ConfigureAwait(true));
    }

    /// <summary>実行物欠損と許可ルート外を未実行として区別します。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task MissingExecutableAndOutsidePathDoNotExecute()
    {
        using MediaTestFiles files = new(testOutput);
        MediaOptions missing = files.Options with { FfmpegPath = Path.Combine(files.Root, "missing.exe") };
        IActionNode node = (IActionNode)MediaNodeRegistration.Create(missing)[0];
        NodeResult result = await node.ExecuteAsync(NodeTestContext.Create(JsonSerializer.Serialize(new { outputPath = Path.Combine(files.Root, "output.wav"), format = "wav" }), NodeValue.FromObject(Path.Combine(files.Root, "missing.wav"))));
        Assert.Equal(EOutcome.NotExecuted, result.Outcome);
        NodeResult outside = await node.ExecuteAsync(NodeTestContext.Create(JsonSerializer.Serialize(new { outputPath = Path.Combine(Path.GetTempPath(), "outside.wav"), format = "wav" }), NodeValue.FromObject(Path.Combine(files.Root, "missing.wav"))));
        Assert.Equal(EOutcome.NotExecuted, outside.Outcome);
    }

    /// <summary>実ffmpegが一時出力を開始してから取消し、終了と未確定出力削除を確認します。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task CancellationStopsActualConversionBeforeCommit()
    {
        using MediaTestFiles files = new(testOutput);
        string input = await files.CreateAsync("video", 20).ConfigureAwait(true);
        string output = Path.Combine(files.Root, "cancelled.webm");
        IActionNode node = (IActionNode)MediaNodeRegistration.Create(files.Options)[1];
        using CancellationTokenSource cancellation = new();
        Task<NodeResult> run = node.ExecuteAsync(NodeTestContext.Create(JsonSerializer.Serialize(new { outputPath = output, format = "webm", width = 1024, height = 1024 }), NodeValue.FromObject(input)), cancellation.Token).AsTask();
        TestTaskLifetime lifetime = new(run, cancellation);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = lifetime.ConfigureAwait(true);
        using CancellationTokenSource wait = new(TimeSpan.FromSeconds(10));
        string partialPattern = "cancelled.webm.*.partial";
        while (!Directory.EnumerateFiles(files.Root, partialPattern).Any() && !run.IsCompleted)
        {
            await Task.Delay(10, wait.Token).ConfigureAwait(true);
        }
        Assert.False(run.IsCompleted);
        await cancellation.CancelAsync().ConfigureAwait(true);
        NodeResult result = await run.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Cancelled, result.Outcome);
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.EnumerateFiles(files.Root, "*.partial"));
    }

    /// <summary>壊れた媒体は出力ファイルを作らず失敗します。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task CorruptInputIsNotACompletedConversion()
    {
        using MediaTestFiles files = new(testOutput);
        string input = Path.Combine(files.Root, "broken.wav");
        await File.WriteAllTextAsync(input, "not audio").ConfigureAwait(true);
        string output = Path.Combine(files.Root, "result.wav");
        IActionNode node = (IActionNode)MediaNodeRegistration.Create(files.Options)[0];
        NodeResult result = await node.ExecuteAsync(NodeTestContext.Create(JsonSerializer.Serialize(new { outputPath = output, format = "wav" }), NodeValue.FromObject(input)));
        Assert.NotEqual(EOutcome.Succeeded, result.Outcome);
        Assert.False(File.Exists(output));
    }
}
