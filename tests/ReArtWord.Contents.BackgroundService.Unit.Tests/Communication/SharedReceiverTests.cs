using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Communication;

/// <summary>同じソケットの購読者を、互いの処理速度から分離します。</summary>
public sealed class SharedReceiverTests
{
    /// <summary>待受中のポートから複数ノードが送信し、同じポートへの返信も受け取れます。</summary>
    /// <returns>送信元ポート、送信内容、返信の実通信確認です。</returns>
    [Fact]
    public async Task SendersShareTheListeningPortAndRepliesStillReachTheReceiverAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        using UdpClient remote = new(new IPEndPoint(IPAddress.Loopback, 0));
        int remotePort = ((IPEndPoint)remote.Client.LocalEndPoint!).Port;
        TaskCompletionSource<int> listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Channel<NodeEmission> replies = Channel.CreateUnbounded<NodeEmission>();
        NodeExecutionContext initial = SocketNodeTests.Context(SocketNodeTests.Settings(new { count = 1 }));
        NodeSourceContext source = new(initial, () => initial, (item, token) => replies.Writer.WriteAsync(item, token), () => false, (status, _) =>
        {
            if (status.State == ESourceState.Listening)
            {
                listening.TrySetResult(IPEndPoint.Parse(status.Message!).Port);
            }
            return ValueTask.CompletedTask;
        });
        Task<NodeResult> receiving = ((ISourceNode)SocketNodeTests.Find("udp.receive")).RunAsync(source, timeout.Token);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable receivingLifetime = new TestTaskLifetime(receiving, timeout).ConfigureAwait(true);
        int localPort = await listening.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
        NodeExecutionContext first = SocketNodeTests.Context(SocketNodeTests.Settings(new { localPort, remoteAddress = "127.0.0.1", remotePort }), input: NodeValue.FromObject("first"));
        NodeExecutionContext second = SocketNodeTests.Context(SocketNodeTests.Settings(new { localPort, remoteAddress = "127.0.0.1", remotePort, broadcast = true }), input: NodeValue.FromObject("second"));
        IActionNode sender = (IActionNode)SocketNodeTests.Find("udp.send");
        NodeResult[] sent = await Task.WhenAll(sender.ExecuteAsync(first, timeout.Token).AsTask(), sender.ExecuteAsync(second, timeout.Token).AsTask()).ConfigureAwait(true);
        Assert.All(sent, result => Assert.Equal(EOutcome.Succeeded, result.Outcome));
        UdpReceiveResult one = await remote.ReceiveAsync(timeout.Token).ConfigureAwait(true);
        UdpReceiveResult two = await remote.ReceiveAsync(timeout.Token).ConfigureAwait(true);
        Assert.Equal(localPort, one.RemoteEndPoint.Port);
        Assert.Equal(localPort, two.RemoteEndPoint.Port);
        Assert.Equal(["first", "second"], new[] { Encoding.UTF8.GetString(one.Buffer), Encoding.UTF8.GetString(two.Buffer) }.Order(StringComparer.Ordinal));
        await remote.SendAsync(Encoding.UTF8.GetBytes("reply"), one.RemoteEndPoint, timeout.Token).ConfigureAwait(true);
        Assert.Equal("reply", Encoding.UTF8.GetString((await replies.Reader.ReadAsync(timeout.Token).ConfigureAwait(true)).Result.Outputs["value"].Binary.Span));
        Assert.Equal(EOutcome.Succeeded, (await receiving.ConfigureAwait(true)).Outcome);
        using UdpClient rebound = new(new IPEndPoint(IPAddress.Loopback, localPort));
        Assert.Equal(localPort, ((IPEndPoint)rebound.Client.LocalEndPoint!).Port);
    }

    /// <summary>遅い購読の上限超過を明示し、速い購読には全件を届け続けます。</summary>
    /// <returns>実UDP通信による購読ごとの障害境界の確認です。</returns>
    [Fact]
    public async Task SlowSubscriberDoesNotBlockAnotherReceiverAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        using CancellationTokenSource stop = new();
        TaskCompletionSource<int> listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource overrun = new(TaskCreationOptions.RunContinuationsAsynchronously);
        NodeExecutionContext slowItem = SocketNodeTests.Context(SocketNodeTests.Settings(new { }));
        NodeSourceContext slow = new(slowItem, () => slowItem, async (_, token) => await release.Task.WaitAsync(token).ConfigureAwait(false), () => false, (status, _) =>
        {
            if (status.State == ESourceState.Listening)
            {
                listening.TrySetResult(IPEndPoint.Parse(status.Message!).Port);
            }
            string overflowCode = "network.receive.overrun";
            if (string.Equals(status.ReasonCode, overflowCode, StringComparison.Ordinal))
            {
                overrun.TrySetResult();
            }
            return ValueTask.CompletedTask;
        }, stop.Token);
        Task<NodeResult> slowRun = ((ISourceNode)SocketNodeTests.Find("udp.receive")).RunAsync(slow, timeout.Token);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable slowRunLifetime = new TestTaskLifetime(slowRun, timeout).ConfigureAwait(true);
        int port = await listening.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
        Channel<NodeEmission> packets = Channel.CreateUnbounded<NodeEmission>();
        TaskCompletionSource fastReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        NodeExecutionContext fastItem = SocketNodeTests.Context(SocketNodeTests.Settings(new { localPort = port }));
        NodeSourceContext fast = new(fastItem, () => fastItem, (item, token) => packets.Writer.WriteAsync(item, token), () => false, (status, _) =>
        {
            if (status.State == ESourceState.Listening)
            {
                fastReady.TrySetResult();
            }
            return ValueTask.CompletedTask;
        }, stop.Token);
        Task<NodeResult> fastRun = ((ISourceNode)SocketNodeTests.Find("udp.receive")).RunAsync(fast, timeout.Token);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable fastRunLifetime = new TestTaskLifetime(fastRun, timeout).ConfigureAwait(true);
        await fastReady.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
        using UdpClient sender = new();
        for (int index = 0; index < 300; index++)
        {
            byte[] bytes = BitConverter.GetBytes(index);
            await sender.SendAsync(bytes, new IPEndPoint(IPAddress.Loopback, port), timeout.Token).ConfigureAwait(true);
            NodeEmission emission = await packets.Reader.ReadAsync(timeout.Token).ConfigureAwait(true);
            Assert.Equal(index, BitConverter.ToInt32(emission.Result.Outputs["value"].Binary.Span));
        }
        release.TrySetResult();
        Task observed = await Task.WhenAny(overrun.Task, slowRun).WaitAsync(timeout.Token).ConfigureAwait(true);
        Assert.Same(overrun.Task, observed);
        await stop.CancelAsync().ConfigureAwait(true);
        await Task.WhenAll(slowRun, fastRun).WaitAsync(timeout.Token).ConfigureAwait(true);
    }
}
