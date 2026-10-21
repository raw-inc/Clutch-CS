using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.Communication.Osc;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Communication;

/// <summary>引数なしOSCの標準形式と旧形式を区別し、途中切れや未知の引数は推測しません。</summary>
public sealed class OscEmptyArgumentTests
{
    private const string LegacyPacket = "2F616C6C2F70696E67000000";
    private const string StandardPacket = LegacyPacket + "2C000000";

    /// <summary>本番で受信したアドレスのみのpingも、空の引数配列として正規化できます。</summary>
    [Theory]
    [InlineData(LegacyPacket)]
    [InlineData(StandardPacket)]
    public void CapturedEmptyMessageNormalizesToExplicitEmptyTypeTags(string bytes)
    {
        JsonElement message = OscPacketCodec.Decode(Convert.FromHexString(bytes));
        Assert.Equal("/all/ping", message.GetProperty("address").GetString());
        Assert.Empty(message.GetProperty("arguments").EnumerateArray());
        Assert.Equal(StandardPacket, Convert.ToHexString(OscPacketCodec.Encode(message)));
    }

    /// <summary>型タグ省略の対応でも、余剰データ・不正padding・引数不足を空引数に丸めません。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("2F616C6C2F70696E670000")]
    [InlineData("2F616C6C2F70696E67000100")]
    [InlineData(LegacyPacket + "00000000")]
    [InlineData(LegacyPacket + "01020304")]
    [InlineData(LegacyPacket + "2C690000")]
    [InlineData(LegacyPacket + "2C780000")]
    [InlineData(StandardPacket + "00000001")]
    [InlineData("2362756E646C6500")]
    public void MissingTagsDoNotPermitUndeclaredOrTruncatedArguments(string bytes)
        => Assert.Throws<FormatException>(() => OscPacketCodec.Decode(Convert.FromHexString(bytes)));

    /// <summary>Bundle内でも子の境界だけで空引数を判断し、時刻と構造を維持します。</summary>
    [Fact]
    public void BundleChildCanOmitEmptyTypeTagsWithoutChangingItsTimetag()
    {
        byte[] packet = Convert.FromHexString("2362756E646C650000000000000000010000000C" + LegacyPacket);
        JsonElement bundle = OscPacketCodec.Decode(packet);
        Assert.Equal(1UL, bundle.GetProperty("timetag").GetUInt64());
        JsonElement child = Assert.Single(bundle.GetProperty("elements").EnumerateArray());
        Assert.Equal("/all/ping", child.GetProperty("address").GetString());
        Assert.Empty(child.GetProperty("arguments").EnumerateArray());
        Assert.Equal("2362756E646C6500000000000000000100000010" + StandardPacket, Convert.ToHexString(OscPacketCodec.Encode(bundle)));
    }

    /// <summary>UDPとTCPの待受で、旧形式の直後にも標準形式を同じ待受から受信できます。</summary>
    /// <returns>実通信と、引数型未指定・明示空配列の両方の確認です。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BothTransportsReceiveEmptyMessagesWithoutStopping(bool tcp, bool typed)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        Channel<NodeEmission> emissions = Channel.CreateUnbounded<NodeEmission>();
        TaskCompletionSource<int> listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Dictionary<string, object> settings = new(StringComparer.Ordinal) { ["count"] = 2 };
        if (typed)
        {
            settings["argumentTypes"] = Array.Empty<string>();
        }
        NodeExecutionContext initial = SocketNodeTests.Context(SocketNodeTests.Settings(settings));
        NodeSourceContext context = new(initial, () => initial, (emission, token) => emissions.Writer.WriteAsync(emission, token), () => false, (status, _) =>
        {
            if (status.State == ESourceState.Listening)
            {
                listening.TrySetResult(IPEndPoint.Parse(status.Message!).Port);
            }
            return ValueTask.CompletedTask;
        });
        string type = tcp ? "osc.tcp.listen"
                          : "osc.udp.receive";
        Task<NodeResult> run = ((ISourceNode)SocketNodeTests.Find(type)).RunAsync(context, deadline.Token);
        int port = await listening.Task.WaitAsync(deadline.Token).ConfigureAwait(true);
        byte[][] packets = [Convert.FromHexString(LegacyPacket), Convert.FromHexString(StandardPacket)];
        if (tcp)
        {
            using TcpClient sender = new();
            await sender.ConnectAsync(IPAddress.Loopback, port, deadline.Token).ConfigureAwait(true);
            using NetworkStream stream = sender.GetStream();
            foreach (byte[] packet in packets)
            {
                byte[] frame = new byte[packet.Length + 4];
                BinaryPrimitives.WriteInt32BigEndian(frame, packet.Length);
                packet.CopyTo(frame, 4);
                await stream.WriteAsync(frame, deadline.Token).ConfigureAwait(true);
            }
            Assert.Equal(EOutcome.Succeeded, (await run.WaitAsync(deadline.Token).ConfigureAwait(true)).Outcome);
        }
        else
        {
            using UdpClient sender = new();
            foreach (byte[] packet in packets)
            {
                await sender.SendAsync(packet, new IPEndPoint(IPAddress.Loopback, port), deadline.Token).ConfigureAwait(true);
            }
            Assert.Equal(EOutcome.Succeeded, (await run.WaitAsync(deadline.Token).ConfigureAwait(true)).Outcome);
        }
        foreach (byte[] packet in packets)
        {
            NodeResult result = (await emissions.Reader.ReadAsync(deadline.Token).ConfigureAwait(true)).Result;
            Assert.True(result.Outcome == EOutcome.Succeeded, Convert.ToHexString(packet) + " " + result.ReasonCode);
            Assert.Equal("/all/ping", result.Outputs["address"].GetString());
            Assert.Empty(result.Outputs["arguments"].Json.EnumerateArray());
        }
    }
}
