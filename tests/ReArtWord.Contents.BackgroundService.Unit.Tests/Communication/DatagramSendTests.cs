using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Communication.ArtNet;
using ReArtWord.Contents.BackgroundService.Nodes.Communication.Osc;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Communication;

/// <summary>UDP送信の実データと開始前拒否を検証します。</summary>
public sealed class DatagramSendTests
{
    /// <summary>各codecの結果が実際のUDPに届きます。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Theory]
    [InlineData("udp.send", "\"hello\"")]
    [InlineData("osc.udp.send", "{\"address\":\"/go\",\"arguments\":[]}")]
    [InlineData("artnet.send", "{\"universe\":1,\"data\":\"AP8=\"}")]
    public async Task DatagramSenderTransmitsExpectedPacketAsync(string type, string inputJson)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        using UdpClient receiver = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        using JsonDocument document = JsonDocument.Parse(inputJson);
        NodeExecutionContext context = SocketNodeTests.Context(SocketNodeTests.Settings(new { remoteAddress = "127.0.0.1", remotePort = port }), input: NodeValue.FromJson(document.RootElement));
        NodeResult result = await ((IActionNode)SocketNodeTests.Find(type)).ExecuteAsync(context, timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        UdpReceiveResult received = await receiver.ReceiveAsync(timeout.Token).ConfigureAwait(true);
        byte[] expected = type switch
        {
            "osc.udp.send" => OscPacketCodec.Encode(document.RootElement),
            "artnet.send" => ArtNetDmxCodec.Encode(document.RootElement),
            _ => Encoding.UTF8.GetBytes("hello"),
        };
        Assert.Equal(expected, received.Buffer);
    }

    /// <summary>最大UDP長超過と未知のNICは送信前に拒否します。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task OversizeOrUnresolvedBindingDoesNotSendAsync()
    {
        IActionNode node = (IActionNode)SocketNodeTests.Find("udp.send");
        NodeExecutionContext oversize = SocketNodeTests.Context(SocketNodeTests.Settings(new { remoteAddress = "127.0.0.1", remotePort = 9 }), input: NodeValue.FromBytes(new byte[65508]));
        Assert.Equal(EOutcome.NotExecuted, (await node.ExecuteAsync(oversize).ConfigureAwait(true)).Outcome);
        NodeExecutionContext unresolved = SocketNodeTests.Context(SocketNodeTests.Settings(new { interfaceId = "not-an-interface", remoteAddress = "127.0.0.1", remotePort = 9 }), input: NodeValue.FromObject("value"));
        Assert.Equal(EOutcome.NotExecuted, (await node.ExecuteAsync(unresolved).ConfigureAwait(true)).Outcome);
    }

    /// <summary>編集用スキーマとValidateが不正型・framingを拒否します。</summary>
    [Fact]
    public void SchemaRejectsInvalidTypesAndOscNonstandardFraming()
    {
        INode udp = SocketNodeTests.Find("udp.send");
        NodeDefinition invalid = new("send", "udp.send", SocketNodeTests.Settings(new { remoteAddress = "127.0.0.1", remotePort = "nine" }));
        Assert.NotEmpty(udp.Validate(invalid));
        INode osc = SocketNodeTests.Find("osc.tcp.request");
        NodeDefinition line = new("send", "osc.tcp.request", SocketNodeTests.Settings(new { remoteAddress = "127.0.0.1", remotePort = 9, framing = "line" }));
        Assert.Contains(osc.Validate(line), issue => string.Equals(issue.Code, "osc.tcp.framing.invalid", StringComparison.Ordinal));
    }
}
