using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Tracing;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Communication;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Observability;

/// <summary>通信ノードのtapが接続線表現を一件の因果情報とともに渡すことを検証します。</summary>
public sealed class TraceCommunicationTests
{
    /// <summary>UDP/OSC/ArtNetの実送信バイトと未加工の記録境界が一致します。</summary>
    /// <returns>実datagramの検証完了。</returns>
    [Theory]
    [InlineData("udp.send", "\"hello\"")]
    [InlineData("osc.udp.send", "{\"address\":\"/go\",\"arguments\":[]}")]
    [InlineData("artnet.send", "{\"universe\":1,\"data\":\"AP8=\"}")]
    public async Task DatagramTraceUsesActualEncodedBytes(string type, string inputJson)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        using UdpClient receiver = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        using JsonDocument input = JsonDocument.Parse(inputJson);
        RecordingTrace trace = new();
        NodeExecutionContext context = SocketNodeTests.Context(SocketNodeTests.Settings(new { remoteAddress = "127.0.0.1", remotePort = port }), input: NodeValue.FromJson(input.RootElement)) with
        {
            TracePolicy = new TracePolicy(true), Trace = trace,
        };
        NodeResult result = await ((IActionNode)SocketNodeTests.Find(type)).ExecuteAsync(context, timeout.Token);
        UdpReceiveResult packet = await receiver.ReceiveAsync(timeout.Token);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(packet.Buffer, trace.Bytes);
        Assert.Equal(context.CausationId, trace.CausationId);
        Assert.True(trace.Outgoing);
    }

    /// <summary>trace側の例外でも送信結果を失敗や再送へ置換しません。</summary>
    /// <returns>副作用が一回だけである検証完了。</returns>
    [Fact]
    public async Task DiagnosticFailureDoesNotChangeSuccessfulDatagramSend()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        using UdpClient receiver = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        NodeExecutionContext context = SocketNodeTests.Context(SocketNodeTests.Settings(new { remoteAddress = "127.0.0.1", remotePort = port }), input: NodeValue.FromObject("once")) with
        {
            TracePolicy = new TracePolicy(true), Trace = new RecordingTrace { Fail = true },
        };
        Assert.Equal(EOutcome.Succeeded, (await ((IActionNode)SocketNodeTests.Find("udp.send")).ExecuteAsync(context, timeout.Token)).Outcome);
        Assert.Equal("once"u8.ToArray(), (await receiver.ReceiveAsync(timeout.Token)).Buffer);
        Assert.Equal(0, receiver.Available);
    }

    /// <summary>通信境界から受け取った内容を記録します。</summary>
    private sealed class RecordingTrace : INodeTrace
    {
        /// <summary>記録不能を再現します。</summary>
        public bool Fail
        {
            get; init;
        }

        /// <summary>実バイト列です。</summary>
        public byte[] Bytes
        {
            get; private set;
        } = [];

        /// <summary>同じ一件の因果識別子です。</summary>
        public Guid CausationId
        {
            get; private set;
        }

        /// <summary>送信側か。</summary>
        public bool Outgoing
        {
            get; private set;
        }

        /// <summary>実際に渡された境界を保持します。</summary>
        /// <returns>完了または意図した診断失敗です。</returns>
        public ValueTask WriteRawAsync(NodeExecutionContext context, string protocol, bool outgoing, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            if (Fail)
            {
                throw new IOException("diagnostic unavailable");
            }
            Bytes = payload.ToArray();
            CausationId = context.CausationId;
            Outgoing = outgoing;
            return ValueTask.CompletedTask;
        }
    }
}
