using System.Net;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Communication;

/// <summary>待受失敗の通知中に停止しても、ポートを解放して取消結果を返すことを確認します。</summary>
public sealed class ListenerCancellationTests
{
    /// <summary>実ポートの競合を起こし、利用不能の通知を処理している間に取り消します。</summary>
    /// <returns>通知からの取消例外を漏らさず、先に確保したポートも残さない確認です。</returns>
    [Theory]
    [InlineData("tcp.listen", true)]
    [InlineData("osc.tcp.listen", true)]
    [InlineData("udp.receive", false)]
    [InlineData("osc.udp.receive", false)]
    [InlineData("artnet.receive", false)]
    [InlineData("http.listen", true)]
    [InlineData("websocket.listen", true)]
    public async Task CancellationDuringUnavailableNotificationReturnsCancelled(string type, bool tcp)
    {
        int[] ports = MultiPortListenerTests.AvailablePair(tcp);
        SocketType socketType = SocketType.Dgram;
        ProtocolType protocol = ProtocolType.Udp;
        if (tcp)
        {
            socketType = SocketType.Stream;
            protocol = ProtocolType.Tcp;
        }
        using Socket occupied = new(AddressFamily.InterNetwork, socketType, protocol);
        occupied.Bind(new IPEndPoint(IPAddress.Loopback, ports[1]));
        if (tcp)
        {
            occupied.Listen();
        }
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        TaskCompletionSource reporting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        NodeExecutionContext initial = SocketNodeTests.Context(SocketNodeTests.Settings(new { localPort = ports }));
        NodeSourceContext context = new(initial, () => initial, (_, _) => ValueTask.CompletedTask, () => false, async (status, token) =>
        {
            if (status.State == ESourceState.Unavailable)
            {
                reporting.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            }
        });
        Task<NodeResult> running = ((ISourceNode)SocketNodeTests.Find(type)).RunAsync(context, cancellation.Token);
        try
        {
            await reporting.Task.WaitAsync(cancellation.Token).ConfigureAwait(true);
        }
        finally
        {
            await cancellation.CancelAsync().ConfigureAwait(true);
        }
        Assert.Equal(EOutcome.Cancelled, (await running.ConfigureAwait(true)).Outcome);
        using Socket released = new(AddressFamily.InterNetwork, socketType, protocol);
        released.Bind(new IPEndPoint(IPAddress.Loopback, ports[0]));
    }
}
