using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Communication;
using ReArtWord.Contents.BackgroundService.Nodes.Communication.Osc;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Communication;

/// <summary>同じUDP待受へ複数ノードを接続し、アドレスと引数を実通信で確認します。</summary>
public sealed class OscWorkflowTests
{
    private static readonly string[] cueTypes = ["i", "s"];
    private static readonly string[] textTypes = ["s"];

    /// <summary>組み立てたパケットを通常の送信ノードへ配線し、実UDPで確認します。</summary>
    /// <returns>空引数と配列引数の実エンジンによる確認です。</returns>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task PacketBuilderFeedsExistingSenderAsync(bool withArguments, bool packetInput, bool dedicatedPort)
    {
        WorkflowFixture fixture = new([.. CommunicationNodeRegistration.Create(new TestSecretStore())]);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        using UdpClient receiver = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        Dictionary<string, NodeValue> inputs = new(StringComparer.Ordinal) { ["address"] = NodeValue.FromObject("/1/start") };
        if (withArguments)
        {
            inputs["input1"] = NodeValue.FromObject(new object?[] { 1, "cue", false, null });
        }
        if (packetInput)
        {
            inputs.Remove("input1");
            string inputPort = dedicatedPort ? "packet"
                                             : "input1";
            inputs[inputPort] = NodeValue.FromJson(JsonSerializer.Deserialize<JsonElement>("""{"address":"/original","arguments":[{"type":"i","value":1},{"type":"s","value":"cue"},{"type":"F","value":false},{"type":"N","value":null}]}"""));
        }
        NodeDefinition packet = new("packet", OscPorts.PacketNodeType, JsonSerializer.SerializeToElement(new { }), inputs);
        NodeDefinition send = new("send", "osc.udp.send", SocketNodeTests.Settings(new { remoteAddress = "127.0.0.1", remotePort = port }), Target: new(EExecutionScope.One, ["a"]));
        CompilationResult compiled = fixture.Compiler.Compile(new("assembled", "assembled", [packet, send], [new("packet", "value", "send", "value")]));
        Assert.Empty(compiled.Issues);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(compiled.Workflow!, cancellationToken: timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        UdpReceiveResult received = await receiver.ReceiveAsync(timeout.Token).ConfigureAwait(true);
        JsonElement decoded = OscPacketCodec.Decode(received.Buffer);
        Assert.Equal("/1/start", decoded.GetProperty("address").GetString());
        Assert.Equal(withArguments ? 4
                                   : 0, decoded.GetProperty("arguments").GetArrayLength());
        Assert.False(receiver.Client.Poll(10000, SelectMode.SelectRead));
    }

    /// <summary>動的引数ポートが検証・実行・リモート実行境界を通過します。</summary>
    /// <returns>実エンジンから送信したパケットの確認です。</returns>
    [Fact]
    public async Task TypedPortsAreAcceptedByCompilerAndExecutorAsync()
    {
        WorkflowFixture fixture = new([.. CommunicationNodeRegistration.Create(new TestSecretStore())]);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        using UdpClient receiver = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        NodeDefinition send = new("send", "osc.udp.send", SocketNodeTests.Settings(new { remoteAddress = "127.0.0.1", remotePort = port, address = "/engine", argumentTypes = textTypes }),
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["argument1"] = NodeValue.FromObject("hello") }, Target: new(EExecutionScope.One, ["a"]));
        CompilationResult compiled = fixture.Compiler.Compile(new("osc-typed", "OSC typed send", [send], []));
        Assert.Empty(compiled.Issues);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(compiled.Workflow!, cancellationToken: timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        UdpReceiveResult packet = await receiver.ReceiveAsync(timeout.Token).ConfigureAwait(true);
        Assert.Equal("hello", OscPacketCodec.Decode(packet.Buffer).GetProperty("arguments")[0].GetProperty("value").GetString());
        CompilationResult missing = fixture.Compiler.Compile(new("osc-missing", "OSC missing argument", [send with { Inputs = null }], []));
        Assert.Contains(missing.Issues, issue => string.Equals(issue.Path, "argument1", StringComparison.Ordinal));
        NodeDefinition noAddress = send with { Settings = SocketNodeTests.Settings(new { remoteAddress = "127.0.0.1", remotePort = port, argumentTypes = textTypes }) };
        Assert.Contains(fixture.Compiler.Compile(new("osc-no-address", "OSC address required", [noAddress], [])).Issues,
            issue => string.Equals(issue.Path, "address", StringComparison.Ordinal));
    }

    /// <summary>同じポートの全件受信とアドレス指定受信は、互いのパケットを奪いません。</summary>
    /// <returns>購読解除後も残った待受が使えることの確認です。</returns>
    [Fact]
    public async Task SharedListenersFilterIndependentlyAndSurviveAnotherListenerStoppingAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        using CancellationTokenSource stop = new();
        Channel<NodeEmission> all = Channel.CreateUnbounded<NodeEmission>();
        Channel<NodeEmission> selected = Channel.CreateUnbounded<NodeEmission>();
        TaskCompletionSource<int> listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<NodeResult> first = Run(new { }, all, listening, stop.Token, timeout.Token);
        int port = await listening.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
        TaskCompletionSource<int> secondListening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<NodeResult> second = Run(new { localPort = port, address = "/cue", argumentTypes = cueTypes, count = 2 }, selected, secondListening, default, timeout.Token);
        Assert.Equal(port, await secondListening.Task.WaitAsync(timeout.Token).ConfigureAwait(true));
        using UdpClient sender = new();
        await SendAsync(sender, port, "/other", timeout.Token).ConfigureAwait(true);
        await SendAsync(sender, port, "/cue", timeout.Token).ConfigureAwait(true);
        NodeEmission other = await all.Reader.ReadAsync(timeout.Token).ConfigureAwait(true);
        Assert.Equal("/other", other.Result.Outputs["address"].GetString());
        Assert.Equal("/cue", (await all.Reader.ReadAsync(timeout.Token).ConfigureAwait(true)).Result.Outputs["address"].GetString());
        NodeEmission cue = await selected.Reader.ReadAsync(timeout.Token).ConfigureAwait(true);
        Assert.Equal(42, cue.Result.Outputs["argument1"].Json.GetInt32());
        Assert.Equal("start", cue.Result.Outputs["argument2"].GetString());
        Assert.False(selected.Reader.TryRead(out _));
        await stop.CancelAsync().ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, (await first.ConfigureAwait(true)).Outcome);
        await SendAsync(sender, port, "/cue", timeout.Token).ConfigureAwait(true);
        Assert.Equal("/cue", (await selected.Reader.ReadAsync(timeout.Token).ConfigureAwait(true)).Result.Outputs["address"].GetString());
        Assert.Equal(EOutcome.Succeeded, (await second.ConfigureAwait(true)).Outcome);
        using UdpClient rebound = new(new IPEndPoint(IPAddress.Loopback, port));
        Assert.Equal(port, ((IPEndPoint)rebound.Client.LocalEndPoint!).Port);
    }

    /// <summary>型違いの一件を失敗にして、次の正常メッセージを受信し続けます。</summary>
    /// <returns>値の自動変換と待受終了が起きないことの確認です。</returns>
    [Fact]
    public async Task TypeMismatchDoesNotCoerceValuesOrStopReceptionAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Channel<NodeEmission> emissions = Channel.CreateUnbounded<NodeEmission>();
        TaskCompletionSource<int> listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<NodeResult> run = Run(new { argumentTypes = textTypes, count = 2 }, emissions, listening, default, timeout.Token);
        int port = await listening.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
        using UdpClient sender = new();
        byte[] wrong = OscPacketCodec.Encode(JsonSerializer.SerializeToElement(new { address = "/value", arguments = new[] { new { type = "i", value = 12 } } }));
        await sender.SendAsync(wrong, new IPEndPoint(IPAddress.Loopback, port), timeout.Token).ConfigureAwait(true);
        NodeResult failure = (await emissions.Reader.ReadAsync(timeout.Token).ConfigureAwait(true)).Result;
        Assert.Equal(EOutcome.Failed, failure.Outcome);
        Assert.Empty(failure.Outputs);
        byte[] valid = OscPacketCodec.Encode(JsonSerializer.SerializeToElement(new { address = "/value", arguments = new[] { new { type = "s", value = "12" } } }));
        await sender.SendAsync(valid, new IPEndPoint(IPAddress.Loopback, port), timeout.Token).ConfigureAwait(true);
        Assert.Equal("12", (await emissions.Reader.ReadAsync(timeout.Token).ConfigureAwait(true)).Result.Outputs["argument1"].GetString());
        Assert.Equal(EOutcome.Succeeded, (await run.ConfigureAwait(true)).Outcome);
    }

    /// <summary>型付き入力で作ったOSCメッセージを、同じ型付き出力で受け取ります。</summary>
    /// <returns>数値精度・Binary・真偽値の送受信確認です。</returns>
    [Fact]
    public async Task TypedSenderPreservesArgumentOrderAndWireTypesAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        string[] types = ["h", "d", "b", "boolean"];
        Channel<NodeEmission> emissions = Channel.CreateUnbounded<NodeEmission>();
        TaskCompletionSource<int> listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<NodeResult> run = Run(new { argumentTypes = types, count = 1 }, emissions, listening, default, timeout.Token);
        int port = await listening.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
        NodeExecutionContext request = SocketNodeTests.Context(SocketNodeTests.Settings(new { remoteAddress = "127.0.0.1", remotePort = port, address = "/typed", argumentTypes = types }));
        Dictionary<string, NodeValue> inputs = new(StringComparer.Ordinal)
        {
            ["argument1"] = NodeValue.FromObject(9007199254740993L),
            ["argument2"] = NodeValue.FromObject(1.25),
            ["argument3"] = NodeValue.FromBytes(new byte[] { 0, 127, 255 }),
            ["argument4"] = NodeValue.FromObject(true),
        };
        request = request with { Inputs = inputs };
        NodeResult sent = await ((IActionNode)SocketNodeTests.Find("osc.udp.send")).ExecuteAsync(request, timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, sent.Outcome);
        NodeResult received = (await emissions.Reader.ReadAsync(timeout.Token).ConfigureAwait(true)).Result;
        Assert.Equal(9007199254740993L, received.Outputs["argument1"].Json.GetInt64());
        Assert.Equal(1.25, received.Outputs["argument2"].Json.GetDouble());
        Assert.Equal(new byte[] { 0, 127, 255 }, received.Outputs["argument3"].Binary.ToArray());
        Assert.True(received.Outputs["argument4"].Json.GetBoolean());
        Assert.Equal("T", received.Outputs["value"].Json.GetProperty("arguments")[3].GetProperty("type").GetString());
        Assert.Equal(EOutcome.Succeeded, (await run.ConfigureAwait(true)).Outcome);
    }

    /// <summary>受信ノードの実際の待受開始を通知する文脈を作成します。</summary>
    /// <returns>受信ノードの寿命です。</returns>
    private static Task<NodeResult> Run(object settings, Channel<NodeEmission> emissions, TaskCompletionSource<int> listening, CancellationToken stopToken, CancellationToken cancellationToken = default)
    {
        NodeExecutionContext initial = SocketNodeTests.Context(SocketNodeTests.Settings(settings));
        NodeSourceContext source = new(initial, () => initial, (item, token) => emissions.Writer.WriteAsync(item, token), () => false, (status, _) =>
        {
            if (status.State == ESourceState.Listening)
            {
                listening.TrySetResult(IPEndPoint.Parse(status.Message!).Port);
            }
            return ValueTask.CompletedTask;
        }, stopToken);
        return ((ISourceNode)SocketNodeTests.Find("osc.udp.receive")).RunAsync(source, cancellationToken);
    }

    /// <summary>二つの引数を持つOSCパケットを送ります。</summary>
    /// <returns>UDPへの送信完了です。</returns>
    private static async Task SendAsync(UdpClient sender, int port, string address, CancellationToken cancellationToken = default)
    {
        byte[] bytes = OscPacketCodec.Encode(JsonSerializer.SerializeToElement(new { address, arguments = new object[] { new { type = "i", value = 42 }, new { type = "s", value = "start" } } }));
        await sender.SendAsync(bytes, new IPEndPoint(IPAddress.Loopback, port), cancellationToken).ConfigureAwait(false);
    }
}
