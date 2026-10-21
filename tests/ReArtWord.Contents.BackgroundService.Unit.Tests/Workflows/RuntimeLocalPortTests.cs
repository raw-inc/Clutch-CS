using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Arithmetic;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Communication;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>実行時に計算した送信元ポートを、一回の通信へ固定します。</summary>
public sealed class RuntimeLocalPortTests
{
    /// <summary>有限通信では加算の出力を接続・保存でき、待受の設定へは広げません。</summary>
    /// <returns>画面と共通の接続判定および実コンパイラーの確認です。</returns>
    [Theory]
    [InlineData("udp.send", true)]
    [InlineData("osc.udp.send", true)]
    [InlineData("artnet.send", true)]
    [InlineData("tcp.request", true)]
    [InlineData("osc.tcp.request", true)]
    [InlineData("http.request", true)]
    [InlineData("websocket.request", true)]
    [InlineData("database.redis", true)]
    [InlineData("udp.receive", false)]
    [InlineData("osc.udp.receive", false)]
    [InlineData("artnet.receive", false)]
    [InlineData("tcp.listen", false)]
    [InlineData("osc.tcp.listen", false)]
    [InlineData("http.listen", false)]
    [InlineData("websocket.listen", false)]
    public async Task ArithmeticLocalPortIsAllowedOnlyForFiniteClients(string type, bool allowed)
    {
        WorkflowFixture fixture = new(new ArithmeticNode("add"), new CommentNode(), RuntimeNetworkDestinationTests.Find(type));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        NodeDefinition add = Add(7000);
        object settings = type switch
        {
            "http.request" => new { url = "http://127.0.0.1/" },
            "websocket.request" => new { url = "ws://127.0.0.1/" },
            "database.redis" => new { remoteAddress = "127.0.0.1", remotePort = 9, operation = "exists" },
            _ => new { remoteAddress = "127.0.0.1", remotePort = 9 },
        };
        if (!allowed)
        {
            settings = new { };
        }
        NodeDefinition target = RuntimeNetworkDestinationTests.Target(type, settings);
        if (!allowed)
        {
            target = target with { Inputs = null };
        }
        WireDefinition wire = new("comment", "value", "target", SettingPorts.Name("localPort"));
        WorkflowDefinition definition = new("ports", "ports", [add, Comment(), target], [new("add", "value", "comment", "value"), wire]);
        Assert.Equal(allowed, WorkflowConnectionTypes.CanReplaceConnection(definition, fixture.Registry.Descriptors, wire));
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        if (allowed)
        {
            Assert.True(compiled.Issues.Count == 0, JsonSerializer.Serialize(compiled.Issues));
            Assert.Contains(compiled.Workflow!.Incoming["target"], candidate => candidate == wire);
        }
        else
        {
            Assert.NotEmpty(compiled.Issues);
        }
    }

    /// <summary>同じ保存済み定義から別々に計算した番号が、実UDPの送信元へ反映されます。</summary>
    /// <returns>保存値へ戻らず、指定ポートとOS自動割当を実通信で確認します。</returns>
    [Fact]
    public async Task OscUsesCalculatedSourcePortOnEachExecution()
    {
        using UdpClient receiver = new(new IPEndPoint(IPAddress.Loopback, 0));
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        int nextPort = 0;
        WorkflowFixture.ActionNode source = new((_, _) => ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject(nextPort - 1))));
        WorkflowFixture fixture = new(source, new ArithmeticNode("add"), new CommentNode(), SocketNodeTests.Find("osc.udp.send"));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        NodeDefinition add = Add(0) with { Inputs = new Dictionary<string, NodeValue> { ["right"] = NodeValue.FromObject(1) } };
        NodeDefinition target = RuntimeNetworkDestinationTests.Target("osc.udp.send", new
        {
            remoteAddress = "127.0.0.1", remotePort = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port, localPort = 65536,
        });
        WorkflowDefinition definition = new("send", "send", [WorkflowFixture.Definition("source"), add, Comment(), target],
            [new("source", "value", "add", "left"), new("add", "value", "comment", "value"), new("comment", "value", "target", SettingPorts.Name("localPort"))]);
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        for (int index = 0; index < 3; index++)
        {
            nextPort = 0;
            if (index < 2)
            {
                using UdpClient available = new(new IPEndPoint(IPAddress.Loopback, 0));
                nextPort = ((IPEndPoint)available.Client.LocalEndPoint!).Port;
            }
            ExecutionReport report = await fixture.Engine.ExecuteAsync(compiled.Workflow!, "source", cancellationToken: timeout.Token).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, report.Outcome);
            UdpReceiveResult received = await receiver.ReceiveAsync(timeout.Token).ConfigureAwait(true);
            Assert.InRange(received.RemoteEndPoint.Port, 1, 65535);
            if (0 < nextPort)
            {
                Assert.Equal(nextPort, received.RemoteEndPoint.Port);
            }
            Assert.Equal("/test", Nodes.Communication.Osc.OscPacketCodec.Decode(received.Buffer).GetProperty("address").GetString());
        }
        Assert.Equal(65536, target.Settings.GetProperty("localPort").GetInt32());
        Assert.Equal(0, receiver.Available);
    }

    /// <summary>表示名を付けても数値と依存関係を維持する中継です。</summary>
    /// <returns>本番WFと同じコメント経由の配線です。</returns>
    private static NodeDefinition Comment() => new("comment", "value.comment", JsonSerializer.SerializeToElement(new { }),
        new Dictionary<string, NodeValue> { ["displayName"] = NodeValue.FromObject("送信ピアで使うポート") });

    /// <summary>整数を返す場合も出力宣言はnumberである、実際の加算ノードです。</summary>
    /// <returns>右辺に1を足す構成です。</returns>
    private static NodeDefinition Add(int left) => new("add", "math.add", JsonSerializer.SerializeToElement(new { }),
        new Dictionary<string, NodeValue> { ["left"] = NodeValue.FromObject(left), ["right"] = NodeValue.FromObject(1) });
}
