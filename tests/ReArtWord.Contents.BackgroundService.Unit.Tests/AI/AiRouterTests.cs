using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.AI;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.AI;

/// <summary>媒体を保持する処理先選択と実接続線を伴う内容分類を検証します。</summary>
public sealed class AiRouterTests
{
    /// <summary>四媒体の値を保持し明示profileを選びます。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Theory]
    [InlineData("text")]
    [InlineData("audio")]
    [InlineData("picture")]
    [InlineData("video")]
    public async Task DestinationRoutesMediaWithoutChangingValue(string kind)
    {
        using MediaTestFiles files = new();
        IActionNode node = (IActionNode)Assert.Single(AiNodeRegistration.Create(new AiTestSecrets(), new AiOptions(), files.Options), item => string.Equals(item.Descriptor.Type, "ai.destination.route", StringComparison.Ordinal));
        NodeValue input = NodeValue.FromObject(new { payload = "media", metadata = 123 });
        Dictionary<string, string> profiles = new(StringComparer.Ordinal) { [kind] = "configured-profile" };
        NodeResult result = await node.ExecuteAsync(NodeTestContext.Create(JsonSerializer.Serialize(new { mediaKind = kind, profiles }), input));
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(kind, result.Route);
        Assert.Equal(input.Json.GetRawText(), result.Outputs["value"].Json.GetRawText());
        Assert.Equal("configured-profile", result.Outputs["profile"].GetString());
    }

    /// <summary>四媒体を実際の公式入力部品へ変換して分類します。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Theory]
    [InlineData("text", "text")]
    [InlineData("audio", "input_audio")]
    [InlineData("picture", "image_url")]
    [InlineData("video", "image_url")]
    public async Task ClassificationUsesMediaAndReportsFrameCoverage(string kind, string contentType)
    {
        using MediaTestFiles files = new();
        NodeValue input = NodeValue.FromObject("Text to classify");
        string textKind = "text";
        if (!string.Equals(kind, textKind, StringComparison.Ordinal))
        {
            input = NodeValue.FromObject(await files.CreateAsync(kind).ConfigureAwait(true));
        }
        AiWireFixture server;
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable serverLifetime = (server = new((_, _) => Task.FromResult(AiWireResponse.Json("""{"choices":[{"finish_reason":"stop","message":{"content":"category-a"}}]}""")))).ConfigureAwait(true);
        Dictionary<string, object> extra = new(StringComparer.Ordinal) { ["mediaKind"] = kind, ["instruction"] = "Classify the input.", ["categories"] = new[] { "category-a", "category-b" } };
        string audioKind = "audio";
        string pictureKind = "picture";
        string videoKind = "video";
        if (string.Equals(kind, audioKind, StringComparison.Ordinal))
        {
            extra["inputFormat"] = "wav";
        }
        if (string.Equals(kind, pictureKind, StringComparison.Ordinal))
        {
            extra["inputFormat"] = "png";
        }
        if (string.Equals(kind, videoKind, StringComparison.Ordinal))
        {
            extra["frameTimesSeconds"] = new[] { 0.0, 0.1 };
        }
        IActionNode node = AiGeneratorTests.Node(server, files, "ai.content.route");
        NodeResult result = await node.ExecuteAsync(NodeTestContext.Create(AiGeneratorTests.Settings(server, extra), input));
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal("success", result.Route);
        Assert.Equal("category-a", result.Outputs["classification"].GetString());
        Assert.Equal(input.Json.GetRawText(), result.Outputs["value"].Json.GetRawText());
        using JsonDocument request = JsonDocument.Parse(Assert.Single(server.Requests).Body);
        Assert.Contains(request.RootElement.GetProperty("messages")[0].GetProperty("content").EnumerateArray(), item => string.Equals(item.GetProperty("type").GetString(), contentType, StringComparison.Ordinal));
        if (string.Equals(kind, videoKind, StringComparison.Ordinal))
        {
            Assert.Equal(2, result.Outputs["coverage"].Json.GetProperty("frameTimesSeconds").GetArrayLength());
            Assert.Equal(2, request.RootElement.GetProperty("messages")[0].GetProperty("content").EnumerateArray().Count(item => string.Equals(item.GetProperty("type").GetString(), "image_url", StringComparison.Ordinal)));
        }
    }

    /// <summary>AIが列挙外回答を返した場合は正常な判定不能へ分けます。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task UnlistedClassificationIsIndeterminate()
    {
        using MediaTestFiles files = new();
        AiWireFixture server;
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable serverLifetime = (server = new((_, _) => Task.FromResult(AiWireResponse.Json("""{"choices":[{"finish_reason":"stop","message":{"content":"other"}}]}""")))).ConfigureAwait(true);
        Dictionary<string, object> extra = new(StringComparer.Ordinal) { ["mediaKind"] = "text", ["instruction"] = "Classify", ["categories"] = new[] { "category-a" } };
        NodeResult result = await AiGeneratorTests.Node(server, files, "ai.content.route").ExecuteAsync(NodeTestContext.Create(AiGeneratorTests.Settings(server, extra), NodeValue.FromObject("input")));
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal("indeterminate", result.Route);
        Assert.Equal(JsonValueKind.Null, result.Outputs["classification"].Json.ValueKind);
    }
}
