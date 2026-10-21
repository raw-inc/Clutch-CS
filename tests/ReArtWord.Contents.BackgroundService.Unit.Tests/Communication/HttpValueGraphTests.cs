using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Communication;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Communication;

/// <summary>型/Key-Value/メソッド/NICの値グラフから、実HTTP待受を自動開始します。</summary>
public sealed class HttpValueGraphTests
{
    /// <summary>原定義を変えず設定を解決し、受信した整数を実エンジンの下流へ届けます。</summary>
    /// <returns>構築・待受・受信・下流実行までの実境界確認です。</returns>
    [Fact]
    public async Task ConstructedTypesAndSettingsStartHttpSourceWithoutMutatingDefinitionAsync()
    {
        Channel<NodeValue> received = Channel.CreateUnbounded<NodeValue>();
        WorkflowFixture.ActionNode sink = new(async (context, cancellation) =>
        {
            await received.Writer.WriteAsync(context.Inputs["value"], cancellation).ConfigureAwait(false);
            return NodeResult.Success();
        }, true);
        WorkflowFixture fixture = new([.. ValuesNodeRegistration.Create(), .. CommunicationNodeRegistration.Create(new TestSecretStore()), sink]);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        string interfaceId = SocketNodeTests.Settings(new { }).GetProperty("interfaceId").GetString()!;
        NodeDefinition server = new("server", "http.listen", JsonSerializer.SerializeToElement(new { path = "/person", count = 1 }),
            Target: new(EExecutionScope.One, ["a"]), StartOnLoad: true);
        WorkflowDefinition definition = new("typed-http", "typed HTTP source",
        [
            new("age-type", "value.type", JsonSerializer.SerializeToElement(new { type = "integer" })),
            new("age-field", "value.keyValue", JsonSerializer.SerializeToElement(new { key = "age" })),
            new("query-type", "value.type", JsonSerializer.SerializeToElement(new { type = "object" })),
            new("method", "http.method", JsonSerializer.SerializeToElement(new { method = "GET" })),
            new("interface", "value.interface", JsonSerializer.SerializeToElement(new { interfaceId })),
            new("address", "value.constant", JsonSerializer.SerializeToElement(new { type = "string", text = "127.0.0.1" })),
            new("port", "value.constant", JsonSerializer.SerializeToElement(new { type = "integer", text = "0" })),
            server,
            WorkflowFixture.Definition("sink"),
        ],
        [
            new("age-type", "value", "age-field", "value"),
            new("age-field", "value", "query-type", "properties"),
            new("query-type", "value", "server", "queryType"),
            new("method", "value", "server", "method"),
            new("interface", "value", "server", SettingPorts.Name("interfaceId")),
            new("address", "value", "server", SettingPorts.Name("localAddress")),
            new("port", "value", "server", SettingPorts.Name("localPort")),
            new("server", "query", "sink", "value"),
        ], true);
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        CompiledWorkflow plan = Assert.IsType<CompiledWorkflow>(compiled.Workflow);
        Assert.Equal(ContractJson.Hash(definition), plan.Revision);
        Assert.Equal(ContractJson.Hash(definition), ContractJson.Hash(plan.Definition));
        Assert.False(server.Settings.TryGetProperty("interfaceId", out _));
        Assert.Equal(interfaceId, plan.OrderedNodes.Single(node => string.Equals(node.Id, "server", StringComparison.Ordinal)).Settings.GetProperty("interfaceId").GetString());
        await fixture.Sources.ApplyAsync(plan, timeout.Token).ConfigureAwait(true);
        string? url = fixture.Sources.GetSources().Single().Status.Message;
        Assert.NotNull(url);
        using HttpClient client = new();
        using HttpResponseMessage response = await client.GetAsync(new Uri(url + "/person?age=25"), timeout.Token).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        NodeValue result = await received.Reader.ReadAsync(timeout.Token).ConfigureAwait(true);
        Assert.Equal(25, result.Json.GetProperty("age").GetInt64());
    }
}
