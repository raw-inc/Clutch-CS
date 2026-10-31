using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Networking;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Communication;

/// <summary>TCP一件途中の停止と通信欠損を区別します。</summary>
public sealed class TcpLifetimeTests
{
    /// <summary>先頭バイト受付後の正常停止でも残りフレームを完了します。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task GracefulStopDrainsAnAlreadyStartedFrameAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        using CancellationTokenSource stop = new();
        Channel<NodeEmission> emissions = Channel.CreateUnbounded<NodeEmission>();
        TaskCompletionSource<int> listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource acquired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        NodeExecutionContext initial = SocketNodeTests.Context(SocketNodeTests.Settings(new { framing = "lengthPrefix" }));
        NodeSourceContext source = new(initial, () =>
        {
            acquired.TrySetResult();
            return initial;
        }, (emission, token) => emissions.Writer.WriteAsync(emission, token), () => false, (status, _) =>
        {
            if (status.State == ESourceState.Listening)
            {
                listening.TrySetResult(IPEndPoint.Parse(status.Message!).Port);
            }
            return ValueTask.CompletedTask;
        }, stop.Token);
        Task<NodeResult> run = ((ISourceNode)SocketNodeTests.Find("tcp.listen")).RunAsync(source, timeout.Token);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable runLifetime = new TestTaskLifetime(run, timeout).ConfigureAwait(true);
        int port = await listening.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
        using TcpClient client = new(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token).ConfigureAwait(true);
        using NetworkStream stream = client.GetStream();
        byte[] frame = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(frame, 4);
        frame[4] = 42;
        await stream.WriteAsync(frame.AsMemory(0, 1), timeout.Token).ConfigureAwait(true);
        await acquired.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
        await stop.CancelAsync().ConfigureAwait(true);
        await stream.WriteAsync(frame.AsMemory(1), timeout.Token).ConfigureAwait(true);
        NodeEmission emission = await emissions.Reader.ReadAsync(timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, emission.Result.Outcome);
        Assert.Equal(new byte[] { 42, 0, 0, 0 }, emission.Result.Outputs["value"].Binary.ToArray());
        Assert.Equal(EOutcome.Succeeded, (await run.ConfigureAwait(true)).Outcome);
    }

    /// <summary>要求本文を送信した後の期限切れと取消は、いずれも結果不明です。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedReplyAfterSendIsUnknownAsync(bool cancel)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(1));
        using CancellationTokenSource requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        NodeExecutionContext request = SocketNodeTests.Context(SocketNodeTests.Settings(new { remoteAddress = "127.0.0.1", remotePort = port, awaitReply = true }), input: NodeValue.FromObject("input"));
        Task<NodeResult> execution = ((IActionNode)SocketNodeTests.Find("tcp.request")).ExecuteAsync(request, requestLifetime.Token).AsTask();
        try
        {
            using TcpClient accepted = await listener.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(true);
            using NetworkStream stream = accepted.GetStream();
            Assert.Equal("input"u8.ToArray(), await TcpFrame.ReadAsync(stream, "lengthPrefix", 1024, timeout.Token).ConfigureAwait(true));

            // 接続だけでなく本文の到着を確認し、返信を返さず通常の操作期限または明示取消を待ちます。
            if (cancel)
            {
                await requestLifetime.CancelAsync().ConfigureAwait(true);
            }
            NodeResult result = await execution.WaitAsync(timeout.Token).ConfigureAwait(true);
            Assert.False(timeout.IsCancellationRequested);
            Assert.Equal(EOutcome.Unknown, result.Outcome);
            Assert.False(listener.Pending());
        }
        finally
        {
            await requestLifetime.CancelAsync().ConfigureAwait(true);
            await execution.ConfigureAwait(true);
        }
    }
}
