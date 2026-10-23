using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.Media;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Media;

/// <summary>変換ノードにも生成ノードと同じ親作成・上書き・スキップを適用します。</summary>
public sealed class MediaOutputPolicyTests(ITestOutputHelper testOutput)
{
    /// <summary>存在しない階層へ実変換し、既存時の各方針で正しい内容を維持します。</summary>
    /// <returns>実ffmpeg・ffprobeで検証した媒体と、未実行のスキップです。</returns>
    [Fact]
    public async Task ConversionCreatesParentsAndSharesOutputChoicesAsync()
    {
        using MediaTestFiles files = new(testOutput);
        string input = await files.CreateAsync("audio").ConfigureAwait(true);
        string outputPath = Path.Combine(files.Root, "new", "nested", "audio.wav");
        IActionNode node = (IActionNode)MediaNodeRegistration.Create(files.Options)[0];
        NodeExecutionContext context = NodeTestContext.Create(JsonSerializer.Serialize(new { outputPath, format = "wav" }), NodeValue.FromObject(input));
        Assert.Equal(EOutcome.Succeeded, (await node.ExecuteAsync(context).ConfigureAwait(true)).Outcome);
        await File.WriteAllTextAsync(outputPath, "old").ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, (await node.ExecuteAsync(context).ConfigureAwait(true)).Outcome);
        byte[] completed = await File.ReadAllBytesAsync(outputPath).ConfigureAwait(true);
        Assert.True(3 < completed.Length);
        NodeExecutionContext skip = NodeTestContext.Create(JsonSerializer.Serialize(new { outputPath, format = "wav", existingFileMode = "skip" }), NodeValue.FromObject(input));
        NodeResult skipped = await node.ExecuteAsync(skip).ConfigureAwait(true);
        Assert.True(skipped.Outputs["value"].Json.GetProperty("skipped").GetBoolean());
        using (FileStream locked = new(outputPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            NodeExecutionContext lockSkip = NodeTestContext.Create(JsonSerializer.Serialize(new { outputPath, format = "wav", lockedFileMode = "skip" }), NodeValue.FromObject(input));
            NodeResult result = await node.ExecuteAsync(lockSkip).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, result.Outcome);
            Assert.Equal("locked", result.Outputs["value"].Json.GetProperty("skipReason").GetString());
        }
        Assert.Equal(completed, await File.ReadAllBytesAsync(outputPath).ConfigureAwait(true));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(outputPath)!, "*.partial"));
    }
}
