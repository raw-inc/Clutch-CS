using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Communication;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Communication;

/// <summary>HTTP受信への型指定・個別項目出力の展開を実通信で確認します。</summary>
public sealed class HttpStructuredTypesTests
{
    /// <summary>JSONは本文自体を解析し、その他はスキーマの保存値にかかわらず元テキストを維持します。</summary>
    /// <returns>不正なJSONの400と、ルート配列・文字列の実HTTP出力です。</returns>
    [Theory]
    [InlineData("json", "schema", "{\"type\":\"array\",\"items\":{\"type\":\"integer\"}}", "[1,2]", "[1,2]")]
    [InlineData("json", "schema", "{\"type\":\"string\",\"minLength\":2}", "\"hello\"", "\"hello\"")]
    [InlineData("json", "auto", "true", "42", "42")]
    [InlineData("auto", "auto", "true", "42", "42")]
    [InlineData("text", "schema", "false", "{\"age\":10}", "\"{\\\"age\\\":10}\"")]
    public async Task FormatAppliesToTheActualBodyRootAsync(string bodyFormat, string mode, string schema, string body, string expected)
    {
        Channel<NodeValue> received = Channel.CreateUnbounded<NodeValue>();
        WorkflowFixture.ActionNode sink = new((context, _) =>
        {
            received.Writer.TryWrite(context.Inputs["value"]);
            return ValueTask.FromResult(NodeResult.Success());
        }, true);
        WorkflowFixture fixture = new([.. CommunicationNodeRegistration.Create(new TestSecretStore()), sink]);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        JsonElement settings = SocketNodeTests.Settings(new { localPort = 0, path = "/root", method = "POST", bodyFormat, bodyMode = mode, bodySchema = schema });
        NodeDefinition server = new("server", "http.listen", settings, Target: new(EExecutionScope.One, ["a"]), StartOnLoad: true);
        CompilationResult compiled = fixture.Compiler.Compile(new("http-root", "HTTP root", [server, WorkflowFixture.Definition("sink")], [new("server", "body", "sink", "value")], true));
        Assert.Empty(compiled.Issues);
        await fixture.Sources.ApplyAsync(compiled.Workflow!, timeout.Token).ConfigureAwait(true);
        Uri url = new(fixture.Sources.GetSources().Single().Status.Message! + "/root");
        using HttpClient client = new();
        string json = "json";
        string automatic = "auto";
        if (string.Equals(bodyFormat, json, StringComparison.Ordinal) || string.Equals(bodyFormat, automatic, StringComparison.Ordinal))
        {
            using StringContent invalid = new("not JSON", System.Text.Encoding.UTF8, "application/json");
            using HttpResponseMessage rejected = await client.PostAsync(url, invalid, timeout.Token).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.False(received.Reader.TryRead(out _));
        }
        using StringContent payload = new(body, System.Text.Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.PostAsync(url, payload, timeout.Token).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        NodeValue actual = await received.Reader.ReadAsync(timeout.Token).ConfigureAwait(true);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(expected), actual.Json));
    }

    /// <summary>全方式で本文から項目を取り出し、手動・スキーマでは不一致を通知前に拒否します。</summary>
    /// <returns>実HTTPの応答とワークフロー入力の確認です。</returns>
    [Theory]
    [InlineData("auto", "legacy", "POST")]
    [InlineData("manual", "legacy", "POST")]
    [InlineData("schema", "legacy", "POST")]
    [InlineData("schema", "auto", "POST")]
    [InlineData("schema", "json", "POST")]
    [InlineData("schema", "json", "PUT")]
    [InlineData("schema", "json", "PATCH")]
    [InlineData("auto", "json", "POST")]
    public async Task BodyModesAndPropertyOutputsUseTheSameContract(string mode, string bodyFormat, string method)
    {
        Channel<NodeValue> received = Channel.CreateUnbounded<NodeValue>();
        WorkflowFixture.ActionNode sink = new(async (context, cancellation) =>
        {
            await received.Writer.WriteAsync(context.Inputs["value"], cancellation).ConfigureAwait(false);
            return NodeResult.Success();
        }, true);
        WorkflowFixture fixture = new([.. CommunicationNodeRegistration.Create(new TestSecretStore()), sink]);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        JsonElement settings = SocketNodeTests.Settings(new
        {
            localPort = 0,
            path = "/person",
            method,
            bodyFormat,
            count = 0,
            bodyMode = mode,
            bodyType = new { type = "object", properties = new { age = new { type = "integer" } } },
            bodySchema = new { type = "object", properties = new { age = new { type = "integer", minimum = 0 } }, required = new List<string> { "age" }, additionalProperties = false },
        });
        NodeDefinition server = new("server", "http.listen", settings, Target: new(EExecutionScope.One, ["a"]), StartOnLoad: true);
        WorkflowDefinition definition = new("http-structured", "HTTP structured", [server, WorkflowFixture.Definition("sink")], [new("server", PropertyPorts.Name("body", "age"), "sink", "value")], true);
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        await fixture.Sources.ApplyAsync(compiled.Workflow!, timeout.Token).ConfigureAwait(true);
        string url = fixture.Sources.GetSources().Single().Status.Message!;
        using HttpClient client = new();
        string automatic = "auto";
        if (!string.Equals(mode, automatic, StringComparison.Ordinal))
        {
            using HttpRequestMessage invalidRequest = new(new HttpMethod(method), url + "/person") { Content = JsonContent.Create(new { age = "wrong" }) };
            using HttpResponseMessage invalid = await client.SendAsync(invalidRequest, timeout.Token).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.False(received.Reader.TryRead(out _));
        }
        using HttpRequestMessage request = new(new HttpMethod(method), url + "/person") { Content = JsonContent.Create(new { age = 10 }) };
        using HttpResponseMessage response = await client.SendAsync(request, timeout.Token).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(10, (await received.Reader.ReadAsync(timeout.Token).ConfigureAwait(true)).Json.GetInt32());
    }
}
