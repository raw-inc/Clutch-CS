using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Networking;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Communication;

/// <summary>複数ポートでの実受信、全体の件数、確保失敗と解放を検証します。</summary>
public sealed class MultiPortListenerTests
{
    /// <summary>範囲と重複した列挙から各ポートを一度だけ待ち受けます。</summary>
    /// <returns>実UDP受信と全購読の終了です。</returns>
    [Theory]
    [InlineData("udp.receive")]
    [InlineData("osc.udp.receive")]
    [InlineData("artnet.receive")]
    public async Task DatagramRangesReceiveOnEveryDistinctPort(string type)
    {
        int[] ports = AvailablePair(false);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Channel<NodeEmission> emissions = Channel.CreateUnbounded<NodeEmission>();
        TaskCompletionSource<string> listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        NodeSourceContext source = Source(new { localPort = $"{ports[0]}-{ports[1]}, {ports[0]}", count = 2 }, emissions, listening);
        Task<NodeResult> run = ((ISourceNode)SocketNodeTests.Find(type)).RunAsync(source, timeout.Token);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable runLifetime = new TestTaskLifetime(run, timeout).ConfigureAwait(true);
        Assert.Equal(2, (await listening.Task.WaitAsync(timeout.Token)).Split(", ", StringSplitOptions.None).Length);
        using UdpClient sender = new(AddressFamily.InterNetwork);
        byte[] packet = type switch
        {
            "osc.udp.receive" => Convert.FromHexString("2F7800002C000000"),
            "artnet.receive" => Nodes.Communication.ArtNet.ArtNetDmxCodec.Encode(System.Text.Json.JsonSerializer.SerializeToElement(new { universe = 1, sequence = 0, physical = 0, data = "AAE=" })),
            _ => [42],
        };
        foreach (int port in ports)
        {
            await sender.SendAsync(packet, new IPEndPoint(IPAddress.Loopback, port), timeout.Token);
            Assert.Equal(EOutcome.Succeeded, (await emissions.Reader.ReadAsync(timeout.Token)).Result.Outcome);
        }
        Assert.Equal(EOutcome.Succeeded, (await run.WaitAsync(timeout.Token)).Outcome);
        Assert.False(emissions.Reader.TryRead(out _));
        AssertReleased(ports, false);
    }

    /// <summary>別ポートのアイドル接続が受信を妨げず、全ポート合計の件数で終了します。</summary>
    /// <returns>接続を開いたままでの受信と正常終了です。</returns>
    [Theory]
    [InlineData("tcp.listen")]
    [InlineData("osc.tcp.listen")]
    public async Task TcpPortsShareTheLimitWithoutAnIdlePortBlockingOthers(string type)
    {
        int[] ports = AvailablePair(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Channel<NodeEmission> emissions = Channel.CreateUnbounded<NodeEmission>();
        TaskCompletionSource<string> listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        NodeSourceContext source = Source(new { localPort = ports, count = 2, framing = "lengthPrefix" }, emissions, listening);
        Task<NodeResult> run = ((ISourceNode)SocketNodeTests.Find(type)).RunAsync(source, timeout.Token);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable runLifetime = new TestTaskLifetime(run, timeout).ConfigureAwait(true);
        await listening.Task.WaitAsync(timeout.Token);
        using TcpClient first = new(), second = new();
        await first.ConnectAsync(IPAddress.Loopback, ports[0], timeout.Token);
        await second.ConnectAsync(IPAddress.Loopback, ports[1], timeout.Token);
        byte[] packet = type switch { "osc.tcp.listen" => Convert.FromHexString("2F7800002C000000"), _ => [42] };
        using NetworkStream secondStream = second.GetStream(), firstStream = first.GetStream();
        await TcpFrame.WriteAsync(secondStream, packet, "lengthPrefix", timeout.Token);
        Assert.Equal(EOutcome.Succeeded, (await emissions.Reader.ReadAsync(timeout.Token)).Result.Outcome);
        await TcpFrame.WriteAsync(firstStream, packet, "lengthPrefix", timeout.Token);
        Assert.Equal(EOutcome.Succeeded, (await emissions.Reader.ReadAsync(timeout.Token)).Result.Outcome);
        Assert.Equal(EOutcome.Succeeded, (await run.WaitAsync(timeout.Token)).Outcome);
        Assert.False(emissions.Reader.TryRead(out _));
        AssertReleased(ports, true);
    }

    /// <summary>HTTPとWebSocketでも整数配列の全ポートへ同じ受付を公開します。</summary>
    /// <returns>実HTTP／WebSocket要求の一件ずつの受付です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpAndWebSocketListenOnAllSelectedPorts(bool websocket)
    {
        int[] ports = AvailablePair(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        Channel<NodeEmission> emissions = Channel.CreateUnbounded<NodeEmission>();
        TaskCompletionSource<string> listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        object settings = new { localPort = new[] { ports[0], ports[1], ports[0] }, count = 2, path = "/input" };
        if (websocket)
        {
            settings = new { localPort = new[] { ports[0], ports[1], ports[0] }, count = 2, replyMode = "fixed", reply = "pong", path = "/input" };
        }
        NodeSourceContext source = Source(settings, emissions, listening);
        string type = websocket ? "websocket.listen"
                                : "http.listen";
        ISourceNode node = (ISourceNode)SocketNodeTests.Find(type);
        Assert.Empty(node.Validate(new("listener", type, source.Initial.Settings)));
        Task<NodeResult> run = node.RunAsync(source, timeout.Token);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable runLifetime = new TestTaskLifetime(run, timeout).ConfigureAwait(true);
        Assert.Equal(2, (await listening.Task.WaitAsync(timeout.Token)).Split(", ", StringSplitOptions.None).Length);
        using HttpClient http = new();
        foreach (int port in ports)
        {
            if (websocket)
            {
                using ClientWebSocket socket = new();
                await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/input"), timeout.Token);
                await socket.SendAsync(Encoding.UTF8.GetBytes("ping").AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
                byte[] buffer = new byte[32];
                ValueWebSocketReceiveResult reply = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
                Assert.Equal("pong", Encoding.UTF8.GetString(buffer, 0, reply.Count));
                Assert.Equal(EOutcome.Succeeded, (await emissions.Reader.ReadAsync(timeout.Token)).Result.Outcome);
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
            }
            else
            {
                using StringContent content = new("{}", Encoding.UTF8, "application/json");
                using HttpResponseMessage response = await http.PostAsync(new Uri($"http://127.0.0.1:{port}/input"), content, timeout.Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal(EOutcome.Succeeded, (await emissions.Reader.ReadAsync(timeout.Token)).Result.Outcome);
            }
        }
        Assert.Equal(EOutcome.Succeeded, (await run.WaitAsync(timeout.Token)).Outcome);
        AssertReleased(ports, true);
    }

    /// <summary>途中の競合でListeningと報告せず、先に開いたポートを解放します。</summary>
    /// <returns>全ポート確保に失敗した待受の取消です。</returns>
    [Theory]
    [InlineData("udp.receive", false)]
    [InlineData("tcp.listen", true)]
    [InlineData("http.listen", true)]
    [InlineData("websocket.listen", true)]
    public async Task PartialBindFailureReleasesEarlierPorts(string type, bool tcp)
    {
        int[] ports = AvailablePair(tcp);
        using Socket busy = Socket(tcp);
        busy.Bind(new IPEndPoint(IPAddress.Loopback, ports[1]));
        if (tcp)
        {
            busy.Listen();
        }
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        TaskCompletionSource unavailable = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool reportedListening = false;
        NodeExecutionContext initial = SocketNodeTests.Context(SocketNodeTests.Settings(new { localPort = ports }));
        NodeSourceContext source = new(initial, () => initial, (_, _) => ValueTask.CompletedTask, () => false, async (status, token) =>
        {
            reportedListening |= status.State == ESourceState.Listening;
            if (status.State == ESourceState.Unavailable)
            {
                unavailable.TrySetResult();
                await resume.Task.WaitAsync(token).ConfigureAwait(true);
            }
        });
        Task<NodeResult> run = ((ISourceNode)SocketNodeTests.Find(type)).RunAsync(source, timeout.Token);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable runLifetime = new TestTaskLifetime(run, timeout).ConfigureAwait(true);
        await unavailable.Task.WaitAsync(timeout.Token);
        Assert.False(reportedListening);
        AssertReleased([ports[0]], tcp);
        resume.TrySetResult();
        await timeout.CancelAsync();
        Assert.Equal(EOutcome.Cancelled, (await run.ConfigureAwait(true)).Outcome);
    }

    /// <summary>複数ポートの終了で、共有ポートを使う別ノードの購読を閉じません。</summary>
    /// <returns>一方の終了後も残るUDP受信の確認です。</returns>
    [Fact]
    public async Task StoppingAPortSetKeepsAnotherSubscriberAlive()
    {
        int[] ports = AvailablePair(false);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Channel<NodeEmission> first = Channel.CreateUnbounded<NodeEmission>(), group = Channel.CreateUnbounded<NodeEmission>();
        TaskCompletionSource<string> firstReady = new(TaskCreationOptions.RunContinuationsAsynchronously), groupReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ISourceNode node = (ISourceNode)SocketNodeTests.Find("udp.receive");
        Task<NodeResult> firstRun = node.RunAsync(Source(new { localPort = ports[0], count = 2 }, first, firstReady), timeout.Token);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstRunLifetime = new TestTaskLifetime(firstRun, timeout).ConfigureAwait(true);
        await firstReady.Task.WaitAsync(timeout.Token);
        Task<NodeResult> groupRun = node.RunAsync(Source(new { localPort = ports, count = 1 }, group, groupReady), timeout.Token);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable groupRunLifetime = new TestTaskLifetime(groupRun, timeout).ConfigureAwait(true);
        await groupReady.Task.WaitAsync(timeout.Token);
        using UdpClient sender = new();
        await sender.SendAsync(new byte[] { 42 }, new IPEndPoint(IPAddress.Loopback, ports[1]), timeout.Token);
        Assert.Equal(EOutcome.Succeeded, (await group.Reader.ReadAsync(timeout.Token)).Result.Outcome);
        Assert.Equal(EOutcome.Succeeded, (await groupRun.ConfigureAwait(true)).Outcome);
        AssertReleased([ports[1]], false);
        for (int index = 0; index < 2; index++)
        {
            await sender.SendAsync(new byte[] { 42 }, new IPEndPoint(IPAddress.Loopback, ports[0]), timeout.Token);
            Assert.Equal(EOutcome.Succeeded, (await first.Reader.ReadAsync(timeout.Token)).Result.Outcome);
        }
        Assert.Equal(EOutcome.Succeeded, (await firstRun.ConfigureAwait(true)).Outcome);
        AssertReleased(ports, false);
    }

    /// <summary>状態通知と一件の出力を持つ受信文脈を用意します。</summary>
    /// <returns>テスト用の独立した待受文脈です。</returns>
    private static NodeSourceContext Source(object settings, Channel<NodeEmission> emissions, TaskCompletionSource<string> listening)
    {
        NodeExecutionContext initial = SocketNodeTests.Context(SocketNodeTests.Settings(settings));
        return new(initial, () => initial, (emission, token) => emissions.Writer.WriteAsync(emission, token), () => false, (status, _) =>
        {
            if (status.State == ESourceState.Listening)
            {
                listening.TrySetResult(status.Message!);
            }
            return ValueTask.CompletedTask;
        });
    }

    /// <summary>実OSで連続した二つの未使用ポートを探します。</summary>
    /// <returns>テスト開始直前に解放する二つの番号です。</returns>
    internal static int[] AvailablePair(bool tcp)
    {
        for (int attempt = 0; attempt < 32; attempt++)
        {
            using Socket first = Socket(tcp), second = Socket(tcp);
            first.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            int port = ((IPEndPoint)first.LocalEndPoint!).Port;
            if (port <= 1)
            {
                continue;
            }
            try
            {
                // 次の自動割当てと競合しやすい後続番号を避け、直前の空き番号と組にします。
                second.Bind(new IPEndPoint(IPAddress.Loopback, port - 1));
                return [port - 1, port];
            }
            catch (SocketException)
            {
                // 隣接ポートが使用中なら、別のOS割当てを試します。
            }
        }
        throw new IOException("連続した試験用ポートを確保できません。");
    }

    /// <summary>同じポートを再確保できることで所有権の解放を確認します。</summary>
    private static void AssertReleased(IEnumerable<int> ports, bool tcp)
    {
        foreach (int port in ports)
        {
            using Socket socket = Socket(tcp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
        }
    }

    /// <summary>試験対象と同じプロトコルのソケットを作成します。</summary>
    /// <returns>呼出側が破棄する実ソケットです。</returns>
    private static Socket Socket(bool tcp)
    {
        if (tcp)
        {
            return new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        }
        return new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    }
}
