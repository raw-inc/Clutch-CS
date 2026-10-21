using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Data;

/// <summary>Base64・data URLの元バイト保持と、失敗・サイズ境界を確認します。</summary>
public sealed class Base64DecodeNodeTests
{
    /// <summary>標準Base64と改行を含むデータを復号し、MIMEも分離します。</summary>
    /// <returns>元バイトと任意MIMEです。</returns>
    [Theory]
    [InlineData("AP+A", "")]
    [InlineData(" AP\r\n+A\t", "")]
    [InlineData("data:image/png;base64,AP+A", "image/png")]
    [InlineData("data:application/octet-stream;base64,AP+A", "application/octet-stream")]
    public async Task DecodesOriginalBytesAsync(string text, string contentType)
    {
        NodeResult result = await new Base64DecodeNode().ExecuteAsync(NodeTestContext.Create(value: NodeValue.FromObject(text))).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(EValueKind.Binary, result.Outputs["value"].Kind);
        Assert.Equal(new byte[] { 0, 255, 128 }, result.Outputs["value"].Binary.ToArray());
        Assert.Equal(contentType, result.Outputs["contentType"].GetString());
    }

    /// <summary>空データも正しく区別し、指定上限ちょうどは受け取ります。</summary>
    /// <returns>境界の結果です。</returns>
    [Fact]
    public async Task EmptyAndExactLimitAreValidAsync()
    {
        Base64DecodeNode node = new();
        NodeResult empty = await node.ExecuteAsync(NodeTestContext.Create(value: NodeValue.FromObject(""))).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, empty.Outcome);
        Assert.Empty(empty.Outputs["value"].Binary.ToArray());
        NodeResult exact = await node.ExecuteAsync(NodeTestContext.Create("{\"maxBytes\":3}", NodeValue.FromObject("AP+A"))).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, exact.Outcome);
        NodeResult exceeded = await node.ExecuteAsync(NodeTestContext.Create("{\"maxBytes\":2}", NodeValue.FromObject("AP+A"))).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, exceeded.Outcome);
        Assert.Empty(exceeded.Outputs);
    }

    /// <summary>不正データ・URL-safe・非Base64のdata URLを勝手に修復しません。</summary>
    /// <returns>部分出力のない変換失敗です。</returns>
    [Theory]
    [InlineData("%bad")]
    [InlineData("AP-A")]
    [InlineData("data:image/png,abc")]
    [InlineData("data:invalid mime;base64,AP+A")]
    [InlineData("data:image/png;base64,!!!!")]
    [InlineData("a")]
    public async Task InvalidTextProducesNoBytesAsync(string text)
    {
        NodeResult result = await new Base64DecodeNode().ExecuteAsync(NodeTestContext.Create(value: NodeValue.FromObject(text))).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Empty(result.Outputs);
    }
}
