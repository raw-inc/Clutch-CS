using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Nodes.Communication;
using ReArtWord.Contents.BackgroundService.Nodes.Communication.Http;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Communication;

/// <summary>実HTTPの非UTF-8バイト・応答情報と、旧WFの互換性を確認します。</summary>
public sealed class HttpResponseFileTests
{
    /// <summary>PNG等の元バイトは通信・envelope保存・解析を通して同一です。</summary>
    /// <returns>不可逆なテキスト化を挟まないことの確認です。</returns>
    [Theory]
    [InlineData("binary", "image/png")]
    [InlineData("auto", "image/png")]
    [InlineData("auto", "application/custom-format")]
    public async Task PreservesBytesAndMetadataAcrossEnvelopeSerializationAsync(string format, string contentType)
    {
        byte[] bytes = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
        NodeResult result = await RequestAsync(format, contentType, bytes).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(EValueKind.Binary, result.Outputs["body"].Kind);
        Assert.Equal(bytes, result.Outputs["body"].Binary.ToArray());
        Assert.Equal(200, result.Outputs["statusCode"].Json.GetInt32());
        Assert.Equal(contentType, result.Outputs["contentType"].GetString());
        Assert.Equal("qr.png", result.Outputs["fileName"].GetString());
        Assert.Equal("one", result.Outputs["headers"].Json.GetProperty("X-Tag")[0].GetString());
        NodeValue envelope = JsonSerializer.Deserialize<NodeValue>(JsonSerializer.Serialize(result.Outputs["value"], ContractJson.Options), ContractJson.Options)!;
        NodeResult parsed = await new HttpResponseParseNode().ExecuteAsync(NodeTestContext.Create(value: envelope)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, parsed.Outcome);
        Assert.Equal(bytes, parsed.Outputs["value"].Binary.ToArray());
        Assert.Equal("qr.png", parsed.Outputs["fileName"].GetString());
        Assert.Equal(contentType, parsed.Outputs["contentType"].GetString());
    }

    /// <summary>旧応答の形を変えず、追加本文口と明示JSON解析からも参照できます。</summary>
    /// <returns>既存配線と新しい本文型の両立です。</returns>
    [Theory]
    [InlineData("legacy", false)]
    [InlineData("text", false)]
    [InlineData("json", true)]
    [InlineData("auto", true)]
    public async Task PreservesLegacyAndParsesJsonWhenSelectedAsync(string format, bool structured)
    {
        string body = "{\"count\":42}";
        NodeResult result = await RequestAsync(format, "application/json", Encoding.UTF8.GetBytes(body)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(body, result.Outputs["value"].Json.GetProperty("body").GetString());
        string legacy = "legacy";
        if (string.Equals(format, legacy, StringComparison.Ordinal))
        {
            Assert.Equal(2, result.Outputs["value"].Json.EnumerateObject().Count());
        }
        if (structured)
        {
            Assert.Equal(42, result.Outputs["body"].Json.GetProperty("count").GetInt32());
        }
        else
        {
            Assert.Equal(body, result.Outputs["body"].GetString());
        }
        NodeResult parsed = await new HttpResponseParseNode().ExecuteAsync(NodeTestContext.Create("{\"format\":\"json\"}", result.Outputs["value"])).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, parsed.Outcome);
        Assert.Equal(42, parsed.Outputs["value"].Json.GetProperty("count").GetInt32());
    }

    /// <summary>非UTF-8のcharsetを尊重し、テキストとして不正なバイトは置換しません。</summary>
    /// <returns>文字化けや壊れたJSONを正常結果としない確認です。</returns>
    [Fact]
    public async Task RespectsCharsetAndFailsInvalidTextAsync()
    {
        NodeResult latin = await RequestAsync("auto", "text/plain; charset=iso-8859-1", new byte[] { 0x63, 0x61, 0x66, 0xe9 }).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, latin.Outcome);
        Assert.Equal("café", latin.Outputs["body"].GetString());
        NodeResult invalid = await RequestAsync("text", "text/plain", new byte[] { 0xff }).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, invalid.Outcome);
        Assert.Empty(invalid.Outputs);
        NodeResult json = await RequestAsync("json", "application/json", Encoding.UTF8.GetBytes("not json")).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, json.Outcome);
        Assert.Empty(json.Outputs);
    }

    /// <summary>認証秘密のechoを新しい出力やenvelopeへ迂回して漏らしません。</summary>
    /// <returns>全出力の機密保護と、画像を改変して成功にしない境界です。</returns>
    [Theory]
    [InlineData("legacy", EOutcome.Succeeded)]
    [InlineData("text", EOutcome.Succeeded)]
    [InlineData("binary", EOutcome.Failed)]
    public async Task NewOutputsDoNotBypassSecretRedactionAsync(string format, EOutcome expected)
    {
        NodeResult result = await RequestAsync(format, "text/plain", Encoding.UTF8.GetBytes("echo:private-token"), true).ConfigureAwait(true);
        Assert.Equal(expected, result.Outcome);
        Assert.DoesNotContain("private-token", JsonSerializer.Serialize(result, ContractJson.Options), StringComparison.Ordinal);
        if (expected == EOutcome.Failed)
        {
            Assert.Equal("http.response.secretEcho", result.ReasonCode);
            Assert.Empty(result.Outputs);
        }
    }

    /// <summary>旧テキスト応答から元の画像バイトを復元できるとは扱いません。</summary>
    /// <returns>誤ったファイルを出力しないことの確認です。</returns>
    [Theory]
    [InlineData("{\"statusCode\":200,\"body\":\"old text\"}")]
    [InlineData("{\"statusCode\":200,\"body\":\"%%%%\",\"bodyEncoding\":\"base64\"}")]
    [InlineData("{\"statusCode\":\"200\",\"body\":\"text\"}")]
    [InlineData("null")]
    public async Task RejectsMissingOrInvalidOriginalBytesAsync(string envelope)
    {
        NodeResult result = await new HttpResponseParseNode().ExecuteAsync(NodeTestContext.Create("{\"format\":\"binary\"}", NodeValue.FromJson(JsonSerializer.Deserialize<JsonElement>(envelope)))).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Empty(result.Outputs);
    }

    /// <summary>製品のHTTPノードから一度だけGETし、任意バイトの応答を返します。</summary>
    /// <returns>実通信を通過したノード結果です。</returns>
    private static async Task<NodeResult> RequestAsync(string format, string contentType, byte[] body, bool authenticated = false)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        using TcpListener server = new(IPAddress.Loopback, 0);
        server.Start();
        string url = "http://127.0.0.1:" + ((IPEndPoint)server.LocalEndpoint).Port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/file";
        string secretId = string.Empty;
        if (authenticated)
        {
            secretId = "api";
        }
        JsonElement settings = SocketNodeTests.Settings(new { url, method = "GET", responseBodyFormat = format, secretId });
        IActionNode node = (IActionNode)CommunicationNodeRegistration.Create(new TestSecretStore("private-token")).Single(item => item.Descriptor.Type is "http.request");
        Task<NodeResult> pending = node.ExecuteAsync(SocketNodeTests.Context(settings), timeout.Token).AsTask();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable pendingLifetime = new TestTaskLifetime(pending, timeout).ConfigureAwait(true);
        using TcpClient client = await server.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(true);
        using NetworkStream stream = client.GetStream();
        using StreamReader reader = new(stream, Encoding.ASCII, false, 1024, true);
        string? line;
        do
        {
            line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(true);
        }
        while (!string.IsNullOrEmpty(line));
        byte[] header = Encoding.ASCII.GetBytes(FormattableString.Invariant($"HTTP/1.1 200 OK\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nContent-Disposition: attachment; filename=\"../../qr.png\"\r\nX-Tag: one\r\nX-Echo: private-token\r\nConnection: close\r\n\r\n"));
        await stream.WriteAsync(header, timeout.Token).ConfigureAwait(true);
        await stream.WriteAsync(body, timeout.Token).ConfigureAwait(true);
        NodeResult result = await pending.ConfigureAwait(true);
        Assert.False(server.Pending());
        return result;
    }
}
