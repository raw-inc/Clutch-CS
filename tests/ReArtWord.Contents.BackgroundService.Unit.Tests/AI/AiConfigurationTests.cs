using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.AI;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.AI;

/// <summary>サービス固有の接続設定と、実際に使う項目・出力を照合します。</summary>
public sealed class AiConfigurationTests
{
    /// <summary>リージョンから決まる接続先を、表示と実行で共有します。</summary>
    [Theory]
    [InlineData("singapore", "https://dashscope-intl.aliyuncs.com/")]
    [InlineData("beijing", "https://dashscope.aliyuncs.com/")]
    public void AlibabaRegionSelectsTheSameEndpointForSpeechAndVideo(string region, string endpoint)
    {
        Assert.Equal(endpoint, AiServiceConnection.Endpoint("ai.video.generate", region)!.AbsoluteUri);
        Assert.Equal(AiServiceConnection.Endpoint("ai.audio.qwen.generate", region), AiServiceConnection.Endpoint("ai.video.generate", region));
        Assert.Null(AiServiceConnection.Endpoint("ai.video.generate", "unknown"));
    }

    /// <summary>存在しない出力を共通テンプレートから増やしません。</summary>
    [Theory]
    [InlineData("ai.text.generate", "value")]
    [InlineData("ai.audio.generate", "value")]
    [InlineData("ai.picture.generate", "value")]
    [InlineData("ai.video.generate", "value,job")]
    [InlineData("ai.content.route", "value,classification,coverage")]
    public void CatalogOnlyExposesResultsProducedByTheService(string type, string ports)
    {
        using MediaTestFiles files = new();
        INode node = AiNodeRegistration.Create(new AiTestSecrets(), new AiOptions(), files.Options).Single(item => string.Equals(item.Descriptor.Type, type, StringComparison.Ordinal));
        ArgumentNullException.ThrowIfNull(ports);
        Assert.Equal(ports.Split(','), node.Descriptor.Outputs.Select(port => port.Name));
    }

    /// <summary>公式接続先を使うノードに任意Endpointを混ぜる設定は拒否します。</summary>
    [Fact]
    public void UnsupportedEndpointAndRegionAreRejectedBeforeDispatch()
    {
        using MediaTestFiles files = new();
        INode node = AiNodeRegistration.Create(new AiTestSecrets(), new AiOptions(), files.Options).Single(item => string.Equals(item.Descriptor.Type, "ai.text.generate", StringComparison.Ordinal));
        JsonElement settings = JsonSerializer.SerializeToElement(new { model = "test", secretRef = "key", interfaceId = "nic", localAddress = "127.0.0.1", endpoint = "https://example.invalid/", region = "beijing" });
        IReadOnlyList<Contracts.Configuration.ValidationIssue> issues = node.Validate(new("n", node.Descriptor.Type, settings));
        Assert.Contains(issues, issue => string.Equals(issue.Path, "/endpoint", StringComparison.Ordinal));
        Assert.Contains(issues, issue => string.Equals(issue.Path, "/region", StringComparison.Ordinal));
    }

    /// <summary>媒体に関係しない形式・フレーム抽出の設定欄を区別します。</summary>
    [Theory]
    [InlineData("text", false, false)]
    [InlineData("audio", true, false)]
    [InlineData("picture", true, false)]
    [InlineData("video", false, true)]
    public void ClassificationFieldsFollowSelectedMedia(string media, bool formatVisible, bool framesVisible)
    {
        using MediaTestFiles files = new();
        NodeDescriptor descriptor = AiNodeRegistration.Create(new AiTestSecrets(), new AiOptions(), files.Options).Single(item => string.Equals(item.Descriptor.Type, "ai.content.route", StringComparison.Ordinal)).Descriptor;
        JsonElement properties = descriptor.SettingsSchema.GetProperty("properties");
        JsonElement settings = JsonSerializer.SerializeToElement(new { mediaKind = media });
        Assert.Equal(formatVisible, NodeSettingVisibility.IsVisible(properties.GetProperty("inputFormat"), settings));
        Assert.Equal(framesVisible, NodeSettingVisibility.IsVisible(properties.GetProperty("frameTimesSeconds"), settings));
    }
}
