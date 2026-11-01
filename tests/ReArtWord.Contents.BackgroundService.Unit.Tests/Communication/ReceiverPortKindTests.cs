using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.Communication.ArtNet;
using ReArtWord.Contents.BackgroundService.Nodes.Communication.Osc;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Communication;

/// <summary>実UDP/TCP受信値と公開ポート型が一致することを確認します。</summary>
public sealed class ReceiverPortKindTests
{
    /// <summary>生バイトとOSC/Art-NetのJSONを同じAny表示へ潰しません。</summary>
    /// <returns>実ループバック通信での型確認です。</returns>
    [Theory]
    [InlineData("udp.receive", "raw", false, EValueKind.Binary, EPortValueKind.Binary)]
    [InlineData("tcp.listen", "raw", true, EValueKind.Binary, EPortValueKind.Binary)]
    [InlineData("osc.udp.receive", "osc", false, EValueKind.Json, EPortValueKind.Json)]
    [InlineData("osc.tcp.listen", "osc", true, EValueKind.Json, EPortValueKind.Json)]
    [InlineData("artnet.receive", "artnet", false, EValueKind.Json, EPortValueKind.Json)]
    public async Task ReceivedValueMatchesDescriptorAsync(string type, string format, bool tcp, EValueKind actualKind, EPortValueKind declaredKind)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Channel<NodeEmission> emissions = Channel.CreateUnbounded<NodeEmission>();
        TaskCompletionSource<int> listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        JsonElement settings = SocketNodeTests.Settings(new { count = 1 });
        if (tcp)
        {
            settings = SocketNodeTests.Settings(new { count = 1, framing = "lengthPrefix" });
        }
        NodeExecutionContext initial = SocketNodeTests.Context(settings);
        NodeSourceContext context = new(initial, () => initial, (emission, token) => emissions.Writer.WriteAsync(emission, token), () => false, (status, _) =>
        {
            if (status.State == ESourceState.Listening)
            {
                listening.TrySetResult(IPEndPoint.Parse(status.Message!).Port);
            }
            return ValueTask.CompletedTask;
        });
        ISourceNode node = (ISourceNode)SocketNodeTests.Find(type);
        Assert.Empty(node.Validate(new(initial.NodeId, type, initial.Settings)));
        Task<NodeResult> running = node.RunAsync(context, timeout.Token);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable runningLifetime = new TestTaskLifetime(running, timeout).ConfigureAwait(true);
        int port = await listening.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
        byte[] bytes = Packet(format);
        if (tcp)
        {
            using TcpClient client = new(AddressFamily.InterNetwork);
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token).ConfigureAwait(true);
            using NetworkStream stream = client.GetStream();
            byte[] frame = new byte[bytes.Length + 4];
            BinaryPrimitives.WriteInt32BigEndian(frame, bytes.Length);
            bytes.CopyTo(frame, 4);
            await stream.WriteAsync(frame, timeout.Token).ConfigureAwait(true);
        }
        else
        {
            using UdpClient sender = new(AddressFamily.InterNetwork);
            await sender.SendAsync(bytes, new IPEndPoint(IPAddress.Loopback, port), timeout.Token).ConfigureAwait(true);
        }
        NodeEmission received = await emissions.Reader.ReadAsync(timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, received.Result.Outcome);
        Assert.Equal(actualKind, received.Result.Outputs["value"].Kind);
        Assert.Equal(declaredKind, node.Descriptor.Outputs.Single(item => string.Equals(item.Name, "value", StringComparison.Ordinal)).ValueKind);
        Assert.Equal(EOutcome.Succeeded, (await running.ConfigureAwait(true)).Outcome);
    }

    /// <summary>実接続線形式の一件を作ります。</summary>
    /// <returns>送信用のパケットです。</returns>
    private static byte[] Packet(string format) => format switch
    {
        "osc" => OscPacketCodec.Encode(JsonSerializer.Deserialize<JsonElement>("{\"address\":\"/value\",\"arguments\":[]}")),
        "artnet" => ArtNetDmxCodec.Encode(JsonSerializer.Deserialize<JsonElement>("{\"universe\":1,\"sequence\":0,\"physical\":0,\"data\":\"AAE=\"}")),
        _ => [42, 12],
    };
}
