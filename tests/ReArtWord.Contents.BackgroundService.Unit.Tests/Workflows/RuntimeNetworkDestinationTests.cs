using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Communication.Osc;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Nodes.Database;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Communication;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>実行時に作った接続先を、保存値へ戻さず通信開始前に検証します。</summary>
public sealed class RuntimeNetworkDestinationTests
{
    /// <summary>数値変換とConcatから接続先IPへ配線し、毎回供給したポートへ実際に送信します。</summary>
    /// <returns>保存設定を変更せず、二回の独立した受信先で確認する結果です。</returns>
    [Fact]
    public async Task ConvertedNumberAndConcatSupplyOscDestinationForEachExecution()
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        using UdpClient first = new(new IPEndPoint(IPAddress.Loopback, 0));
        using UdpClient second = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)first.Client.LocalEndPoint!).Port;
        WorkflowFixture.ActionNode source = new((_, _) => ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject(new { suffix = 1, port }))));
        WorkflowFixture fixture = new(source, new ConvertNode(), new ConcatNode(), SocketNodeTests.Find("osc.udp.send"));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        NodeValue packet = NodeValue.FromObject(new { address = "/runtime-ip", arguments = Array.Empty<object>() });
        NodeDefinition convert = new("convert", "data.convert", JsonSerializer.SerializeToElement(new { targetType = "string" }));
        NodeDefinition concat = new("concat", "data.concat", JsonSerializer.SerializeToElement(new { }),
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["input1"] = NodeValue.FromObject("127.0.0.") });
        NodeDefinition send = new("send", "osc.udp.send", SocketNodeTests.Settings(new { remoteAddress = "", remotePort = 0 }),
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = packet }, Target: new(EExecutionScope.One, ["a"]));
        WorkflowDefinition definition = new("runtime-ip", "Runtime IP", [WorkflowFixture.Definition("source"), convert, concat, send],
            [new("source", "value", "convert", "value", Path: "/suffix"), new("convert", "value", "concat", "input2"),
                new("concat", "value", "send", SettingPorts.Name("remoteAddress")), new("source", "value", "send", SettingPorts.Name("remotePort"), Path: "/port")]);
        Assert.All(definition.Wires, wire => Assert.True(WorkflowConnectionTypes.CanConnect(definition, fixture.Registry.Descriptors, wire), wire.ToString()));
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        foreach (UdpClient receiver in new[] { first, second })
        {
            port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
            ExecutionReport report = await fixture.Engine.ExecuteAsync(compiled.Workflow!, "source", cancellationToken: deadline.Token).ConfigureAwait(true);
            Assert.True(report.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(report));
            UdpReceiveResult received = await receiver.ReceiveAsync(deadline.Token).ConfigureAwait(true);
            Assert.Equal(OscPacketCodec.Encode(packet.Json), received.Buffer);
        }
        Assert.Equal("", definition.Nodes[3].Settings.GetProperty("remoteAddress").GetString());
        Assert.Equal(0, definition.Nodes[3].Settings.GetProperty("remotePort").GetInt32());
    }

    /// <summary>同じ設定ポートを持つ通信ノード全体で、実行時の接続先を保存・コンパイルできます。</summary>
    /// <returns>固定値が未入力でも、接続した項目だけを実行時検証へ延期する確認です。</returns>
    [Theory]
    [InlineData("udp.send", "remoteAddress")]
    [InlineData("osc.udp.send", "remoteAddress")]
    [InlineData("artnet.send", "remoteAddress")]
    [InlineData("tcp.request", "remoteAddress")]
    [InlineData("osc.tcp.request", "remoteAddress")]
    [InlineData("database.redis", "remoteAddress")]
    [InlineData("http.request", "url")]
    [InlineData("websocket.request", "url")]
    public async Task RelatedClientsCompileWithWiredDestinations(string type, string property)
    {
        WorkflowFixture.ActionNode source = new((_, _) => ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject(new { address = "127.0.0.1", port = 9 }))));
        WorkflowFixture fixture = new(source, Find(type));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        string redis = "database.redis";
        object settings = string.Equals(type, redis, StringComparison.Ordinal) ? new { operation = "exists" }
                                                                               : new { };
        NodeDefinition target = Target(type, settings);
        List<WireDefinition> wires = [new("source", "value", "target", SettingPorts.Name(property), Path: "/address")];
        string addressProperty = "remoteAddress";
        if (string.Equals(property, addressProperty, StringComparison.Ordinal))
        {
            wires.Add(new("source", "value", "target", SettingPorts.Name("remotePort"), Path: "/port"));
        }
        WorkflowDefinition definition = new("related", "Related destinations", [WorkflowFixture.Definition("source"), target], wires);
        Assert.All(wires, wire => Assert.True(WorkflowConnectionTypes.CanReplaceConnection(definition, fixture.Registry.Descriptors, wire), wire.ToString()));
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        Assert.Equal(wires.Count, compiled.Workflow!.Incoming["target"].Count);
        Assert.False(compiled.Workflow.FindNode("target")!.Settings.TryGetProperty(property, out _));
    }

    /// <summary>未供給・不正IP・型違い・不正ポートを、古い保存先へ送らずに拒否します。</summary>
    /// <returns>副作用を開始しない結果と、保存先で未受信である確認です。</returns>
    [Theory]
    [InlineData("remoteAddress", "missing")]
    [InlineData("remoteAddress", "null")]
    [InlineData("remoteAddress", "number")]
    [InlineData("remoteAddress", "empty")]
    [InlineData("remoteAddress", "binary")]
    [InlineData("remoteAddress", "bad-ip")]
    [InlineData("remoteAddress", "ipv6")]
    [InlineData("remotePort", "empty")]
    [InlineData("remotePort", "zero")]
    [InlineData("remotePort", "too-large")]
    [InlineData("remotePort", "fraction")]
    [InlineData("localPort", "missing")]
    [InlineData("localPort", "null")]
    [InlineData("localPort", "empty")]
    [InlineData("localPort", "negative")]
    [InlineData("localPort", "too-large")]
    [InlineData("localPort", "fraction")]
    [InlineData("localPort", "binary")]
    public async Task InvalidRuntimeEndpointNeverUsesTheStoredDestination(string property, string mode)
    {
        using UdpClient receiver = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        NodeValue value = mode switch
        {
            "null" => NodeValue.Null,
            "number" => NodeValue.FromObject(42),
            "zero" => NodeValue.FromObject(0),
            "negative" => NodeValue.FromObject(-1),
            "too-large" => NodeValue.FromObject(65536),
            "fraction" => NodeValue.FromObject(1.5),
            "binary" => NodeValue.FromBytes(new byte[] { 127, 0, 0, 1 }),
            "bad-ip" => NodeValue.FromObject("not-an-ip"),
            "ipv6" => NodeValue.FromObject("::1"),
            _ => NodeValue.FromObject(""),
        };
        string missing = "missing";
        NodeResult supplied = NodeResult.Success(value);
        if (string.Equals(mode, missing, StringComparison.Ordinal))
        {
            supplied = NodeResult.Success();
        }
        WorkflowFixture.ActionNode source = new((_, _) => ValueTask.FromResult(supplied));
        WorkflowFixture fixture = new(source, Find("udp.send"));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = new("guard", "Endpoint guard", [WorkflowFixture.Definition("source"), Target("udp.send", new { remoteAddress = "127.0.0.1", remotePort = port })],
            [new("source", "value", "target", SettingPorts.Name(property)), new("source", "result", "target", "trigger")]);
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(compiled.Workflow!, "source").ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, Assert.Single(report.Nodes, node => node.NodeId is "target").Outcome);
        Assert.Equal(0, receiver.Available);
    }

    /// <summary>URL固有の制約も接続値に適用し、不適切な方式・資格情報・fragmentで開始しません。</summary>
    /// <returns>スキーマのstring判定だけで外部通信へ進まない確認です。</returns>
    [Theory]
    [InlineData("http.request", "file:///test")]
    [InlineData("websocket.request", "http://127.0.0.1/")]
    [InlineData("websocket.request", "ws://user:secret@127.0.0.1/")]
    [InlineData("websocket.request", "ws://127.0.0.1/#fragment")]
    public async Task DynamicUrlsKeepProtocolValidation(string type, string address)
    {
        WorkflowFixture.ActionNode source = new((_, _) => ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject(address))));
        WorkflowFixture fixture = new(source, Find(type));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = new("url-guard", "URL guard", [WorkflowFixture.Definition("source"), Target(type, new { })],
            [new("source", "value", "target", SettingPorts.Name("url"))]);
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(compiled.Workflow!, "source").ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, Assert.Single(report.Nodes, node => node.NodeId is "target").Outcome);
    }

    /// <summary>有限送信のNICは動的に供給し、待受設定と未接続の必須項目は事前に確定します。</summary>
    /// <returns>項目間の制約も、動的設定の解決後かつ通信前に検証する確認です。</returns>
    [Fact]
    public async Task UnconnectedEndpointAndSourceBindingsStillFailBeforeExecution()
    {
        string supplied = "127.0.0.1";
        WorkflowFixture.ActionNode source = new((_, _) => ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject(supplied))));
        WorkflowFixture fixture = new(source, Find("udp.send"), Find("udp.receive"), Find("osc.tcp.request"), Find("http.request"));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        NodeDefinition target = Target("udp.send", new { remoteAddress = "", remotePort = 9 });
        WorkflowDefinition definition = new("fixed", "Fixed settings", [WorkflowFixture.Definition("source"), target], []);
        Assert.Contains(fixture.Compiler.Compile(definition).Issues, issue => issue.Code is "network.remoteAddress.invalid");
        foreach (string property in new[] { "interfaceId", "localAddress" })
        {
            Assert.True(WorkflowConnectionTypes.CanReplaceConnection(definition, fixture.Registry.Descriptors, new("source", "value", "target", SettingPorts.Name(property))));
        }
        definition = definition with { Nodes = [definition.Nodes[0], Target("udp.receive", new { }) with { Inputs = null }] };
        Assert.False(WorkflowConnectionTypes.CanReplaceConnection(definition, fixture.Registry.Descriptors, new("source", "value", "target", SettingPorts.Name("localAddress"))));
        definition = definition with
        {
            Nodes = [definition.Nodes[0], Target("osc.tcp.request", new { remotePort = 9, framing = "line" })],
            Wires = [new("source", "value", "target", SettingPorts.Name("remoteAddress"))],
        };
        Assert.Contains(fixture.Compiler.Compile(definition).Issues, issue => issue.Code is "settings.value" && issue.Path is "/framing");
        definition = definition with
        {
            Nodes = [definition.Nodes[0], Target("http.request", new { success = "body" })],
            Wires = [new("source", "value", "target", SettingPorts.Name("url"))],
        };
        supplied = "http://127.0.0.1:1/";
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(compiled.Workflow!, "source").ConfigureAwait(true);
        NodeResult result = Assert.Single(Assert.Single(report.Nodes, node => node.NodeId is "target").Targets).Result;
        Assert.Equal(EOutcome.NotExecuted, result.Outcome);
        Assert.Equal("http.success.body.required", result.ReasonCode);
    }

    /// <summary>実ノードの登録から接続先の検証を取得します。</summary>
    /// <returns>テスト用の型宣言で置き換えない実装です。</returns>
    internal static INode Find(string type)
    {
        string redis = "database.redis";
        if (string.Equals(type, redis, StringComparison.Ordinal))
        {
            return DatabaseNodeRegistration.Create(new TestSecretStore(), new([])).Single(node => string.Equals(node.Descriptor.Type, type, StringComparison.Ordinal));
        }
        return SocketNodeTests.Find(type);
    }

    /// <summary>送信元NICと、方式に応じた最小入力を持つ接続先ノードを作ります。</summary>
    /// <returns>静的接続先だけが未指定の定義です。</returns>
    internal static NodeDefinition Target(string type, object settings)
    {
        Dictionary<string, NodeValue> inputs = type switch
        {
            "database.redis" => new(StringComparer.Ordinal) { ["key"] = NodeValue.FromObject("test") },
            "osc.udp.send" or "osc.tcp.request" => new(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(new { address = "/test", arguments = Array.Empty<object>() }) },
            "artnet.send" => new(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(new { universe = 1, data = "AP8=" }) },
            _ => new(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject("test") },
        };
        return new("target", type, SocketNodeTests.Settings(settings), inputs, Target: new(EExecutionScope.One, ["a"]));
    }
}
