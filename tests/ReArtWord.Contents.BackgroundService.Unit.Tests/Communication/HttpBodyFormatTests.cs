using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Communication;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Communication;

/// <summary>MIME判定・明示指定・フォールバックと、実通信でのバイト列保持を検証します。</summary>
public sealed class HttpBodyFormatTests
{
    /// <summary>ヘッダーを認識できる場合だけ自動判定し、明示指定を優先します。</summary>
    [Theory]
    [InlineData("auto", "binary", "Application/JSON; charset=utf-8", "json", "Application/JSON")]
    [InlineData("auto", "binary", "application/problem+json", "json", "application/problem+json")]
    [InlineData("auto", "binary", "text/plain", "text", "text/plain")]
    [InlineData("auto", "text", "image/png", "binary", "image/png")]
    [InlineData("auto", "text", "audio/wav", "binary", "audio/wav")]
    [InlineData("auto", "text", "application/octet-stream", "binary", "application/octet-stream")]
    [InlineData("auto", "binary", null, "binary", "image/png")]
    [InlineData("auto", "json", "application/x-custom", "json", "application/json")]
    [InlineData("auto", "text", "invalid", "text", "text/plain")]
    [InlineData("binary", "text", "application/json", "binary", "image/png")]
    [InlineData("json", "binary", "text/plain", "json", "application/json")]
    [InlineData("text", "binary", "application/json", "text", "text/plain")]
    public void MimeAndExplicitPriorityAreDeterministic(string bodyFormat, string bodyFallback, string? header, string format, string media)
    {
        JsonElement settings = JsonSerializer.SerializeToElement(new { bodyFormat, bodyFallback, bodyMediaType = "image/png" });
        Assert.Equal((format, media), HttpBodyFormats.Resolve(settings, header));
    }

    /// <summary>UTF-8不正byteを失わず、MIMEと独立のバイト列出力へ渡します。</summary>
    /// <returns>POST/PUT/PATCHの実HTTPと後続入力の一致です。</returns>
    [Theory]
    [InlineData("auto", "image/png", "POST", "image/png")]
    [InlineData("auto", "audio/wav", "PUT", "audio/wav")]
    [InlineData("auto", null, "PATCH", "application/octet-stream")]
    [InlineData("binary", "text/plain", "POST", "application/octet-stream")]
    [InlineData("auto", "text/plain", "POST", "text/plain")]
    public async Task BinaryBytesAreNotDecodedAsTextAsync(string bodyFormat, string? header, string method, string expectedMime)
    {
        Channel<IReadOnlyDictionary<string, NodeValue>> received = Channel.CreateUnbounded<IReadOnlyDictionary<string, NodeValue>>();
        WorkflowFixture.ActionNode sink = new((context, _) =>
        {
            received.Writer.TryWrite(context.Inputs);
            return ValueTask.FromResult(NodeResult.Success());
        }, true);
        WorkflowFixture fixture = new([.. CommunicationNodeRegistration.Create(new TestSecretStore()), sink]);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        JsonElement settings = SocketNodeTests.Settings(new { localPort = 0, path = "/binary", method, bodyFormat, bodyFallback = "binary", bodyMediaType = "application/octet-stream", bodyMode = "schema", bodySchema = false });
        NodeDefinition server = new("server", "http.listen", settings, Target: new(EExecutionScope.One, ["a"]), StartOnLoad: true);
        WorkflowDefinition flow = new("binary-body", "Binary body", [server, WorkflowFixture.Definition("sink")], [new("server", "body", "sink", "value"), new("server", "bodyContentType", "sink", "trigger")], true);
        CompilationResult compiled = fixture.Compiler.Compile(flow);
        Assert.Empty(compiled.Issues);
        await fixture.Sources.ApplyAsync(compiled.Workflow!, timeout.Token).ConfigureAwait(true);
        Uri url = new(fixture.Sources.GetSources().Single().Status.Message! + "/binary");
        byte[] data = [0x00, 0xff, 0xfe, 0x80, 0x01];
        string textMime = "text/plain";
        bool text = string.Equals(expectedMime, textMime, StringComparison.Ordinal);
        if (text)
        {
            data = System.Text.Encoding.UTF8.GetBytes("unparsed text");
        }
        using HttpClient client = new();
        using HttpRequestMessage request = new(new HttpMethod(method), url) { Content = new ByteArrayContent(data) };
        if (header is not null)
        {
            request.Content.Headers.ContentType = new(header);
        }
        using HttpResponseMessage response = await client.SendAsync(request, timeout.Token).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        IReadOnlyDictionary<string, NodeValue> actual = await received.Reader.ReadAsync(timeout.Token).ConfigureAwait(true);
        if (text)
        {
            Assert.Equal("unparsed text", actual["value"].GetString());
        }
        else
        {
            Assert.Equal(EValueKind.Binary, actual["value"].Kind);
            Assert.Equal(data, actual["value"].Binary.ToArray());
        }
        Assert.Equal(expectedMime, actual["trigger"].GetString());
        Assert.False(PropertyPorts.TryRead(new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["body"] = actual["value"] }, "body/age", out _));
    }
}
