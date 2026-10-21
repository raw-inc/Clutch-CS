using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Communication;
using ReArtWord.Contents.BackgroundService.Nodes.Communication.Osc;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Communication;

/// <summary>接続した型定義が実通信まで同じポート契約を保つことを確認します。</summary>
public sealed class OscConnectedTypesTests
{
    private static readonly string[] staleTypes = ["s", "s"];

    /// <summary>接続した型定義で解析の出力口を確定し、その値を後続へ渡します。</summary>
    /// <returns>カタログ・コンパイル・通常実行の一巡です。</returns>
    [Fact]
    public async Task ParserUsesConnectedTypesAndForwardsTheTypedValueAsync()
    {
        WorkflowFixture fixture = new([.. ValuesNodeRegistration.Create(), .. CommunicationNodeRegistration.Create(new TestSecretStore()), new Nodes.Control.PassNode()]);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        NodeDefinition type = WorkflowFixture.Definition("type", "value.type", """{"type":"int64"}""");
        NodeDefinition parse = new("parse", OscPorts.ParseNodeType, JsonSerializer.SerializeToElement(new { argumentTypes = staleTypes }),
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(new { address = "/large", arguments = new[] { new { type = "h", value = long.MaxValue } } }) });
        NodeDefinition after = WorkflowFixture.Definition("after", "control.pass");
        WireDefinition typeWire = new(type.Id, "value", parse.Id, SettingPorts.Name("argumentTypes"));
        WireDefinition argumentWire = new(parse.Id, "argument1", after.Id, "value");
        WorkflowDefinition workflow = new("parsed", "Parsed", [type, parse, after], [typeWire, argumentWire]);
        Assert.True(WorkflowConnectionTypes.CanConnect(workflow, fixture.Registry.Descriptors, typeWire));
        Assert.True(WorkflowConnectionTypes.CanConnect(workflow, fixture.Registry.Descriptors, argumentWire));
        NodeDescriptor descriptor = Assert.Single(fixture.Registry.Descriptors, item => string.Equals(item.Type, OscPorts.ParseNodeType, StringComparison.Ordinal));
        Assert.True(descriptor.IsValueOperation);
        Assert.True(descriptor.CanEvaluateStatically);
        CompilationResult compiled = fixture.Compiler.Compile(workflow);
        Assert.Empty(compiled.Issues);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(compiled.Workflow!).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        NodeResult result = Assert.Single(Assert.Single(report.Nodes, node => string.Equals(node.NodeId, after.Id, StringComparison.Ordinal)).Targets).Result;
        Assert.Equal(long.MaxValue, result.Outputs["value"].Json.GetInt64());
        Assert.Equal(2, parse.Settings.GetProperty("argumentTypes").GetArrayLength());
    }

    /// <summary>保存されている古い引数型より接続した型を優先し、正確なInt64を送信します。</summary>
    /// <returns>コンパイル・実行・OSCパケットの確認です。</returns>
    [Theory]
    [InlineData("integer", 1L, "i")]
    [InlineData("int64", 1L, "h")]
    [InlineData("int64", long.MaxValue, "h")]
    public async Task ConnectedTypeDefinesPortsBeforeCompilationAndSendsSelectedIntegerPrecisionAsync(string typeName, long value, string tag)
    {
        WorkflowFixture fixture = new([.. ValuesNodeRegistration.Create(), .. CommunicationNodeRegistration.Create(new TestSecretStore())]);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        using UdpClient receiver = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        NodeDefinition type = new("type", "value.type", JsonSerializer.SerializeToElement(new { type = typeName }));
        NodeDefinition send = new("send", "osc.udp.send", SocketNodeTests.Settings(new { remoteAddress = "127.0.0.1", remotePort = port, address = "/typed", argumentTypes = staleTypes }),
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["argument1"] = NodeValue.FromObject(value) }, Target: new(EExecutionScope.One, ["a"]));
        WireDefinition wire = new("type", "value", "send", SettingPorts.Name("argumentTypes"));
        WorkflowDefinition workflow = new("osc-connected", "OSC connected types", [type, send], [wire]);
        Assert.True(WorkflowConnectionTypes.CanConnect(workflow, fixture.Registry.Descriptors, wire));
        CompilationResult compiled = fixture.Compiler.Compile(workflow);
        Assert.Empty(compiled.Issues);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(compiled.Workflow!, cancellationToken: timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        UdpReceiveResult packet = await receiver.ReceiveAsync(timeout.Token).ConfigureAwait(true);
        JsonElement argument = Assert.Single(OscPacketCodec.Decode(packet.Buffer).GetProperty("arguments").EnumerateArray());
        Assert.Equal(tag, argument.GetProperty("type").GetString());
        Assert.Equal(value, argument.GetProperty("value").GetInt64());
        Assert.Equal(2, send.Settings.GetProperty("argumentTypes").GetArrayLength());
    }

    /// <summary>対応しない型をOSCへ黙って変換しません。</summary>
    [Theory]
    [InlineData("any")]
    [InlineData("decimal")]
    [InlineData("array")]
    [InlineData("object")]
    public void UnsupportedTypesCannotDefineOscArguments(string type)
    {
        Assert.Null(OscPorts.ArgumentTypes(JsonSerializer.SerializeToElement(new { type })));
    }

    /// <summary>順序付き型配列は異なるwire型を保持します。</summary>
    [Fact]
    public void OrderedDefinitionsAndExplicitWireTagsPreserveTheirMeaning()
    {
        JsonElement types = JsonSerializer.Deserialize<JsonElement>("""[{"type":"string"},{"type":"number"},"i",{"type":"boolean"}]""");
        Assert.Equal(["s", "d", "i", "boolean"], OscPorts.ArgumentTypes(types));
        Assert.Null(OscPorts.ArgumentTypes(JsonSerializer.Deserialize<JsonElement>("""[{"type":"string","optional":true}]""")));
    }
}
