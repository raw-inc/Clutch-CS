using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>通信断で入力と映像の所有者が残らないことを実streamで確認します。</summary>
[Collection("TimingSensitive")]
public sealed class DesktopLivenessTests
{
    /// <summary>生存通知は入力へ流さず、通知停止で両方向を閉じます。</summary>
    /// <returns>実TCPとpipeの終了確認です。</returns>
    // 抑制理由: Assertのdelegate内とfinallyでbridgeをawaitし、stream解放前の終了を確認します。
    [Fact, SuppressMessage("Reliability", "CA2025", Justification = "finallyで通信taskの終了をawaitします。") ]
    public async Task HeartbeatIsNotAnInputAndItsLossClosesBothDirections()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(12));
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        using TcpClient remote = new();
        await remote.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token).ConfigureAwait(true);
        using TcpClient accepted = await listener.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(true);
        using NetworkStream network = accepted.GetStream();
        using NetworkStream sender = remote.GetStream();
        string name = "desktop-liveness-" + Guid.NewGuid().ToString("N");
        using NamedPipeServerStream worker = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using NamedPipeClientStream reader = new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using Task attached = worker.WaitForConnectionAsync(timeout.Token);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable attachmentLifetime = new TestTaskLifetime(attached, timeout).ConfigureAwait(true);
        await reader.ConnectAsync(timeout.Token).ConfigureAwait(true);
        await attached.ConfigureAwait(true);
        using Task bridge = DesktopSession.BridgeAsync(network, worker, timeout.Token);
        try
        {
            byte[] heartbeat = new byte[24];
            byte[] input = new byte[24];
            BinaryPrimitives.WriteInt32BigEndian(input, 4);
            BinaryPrimitives.WriteInt32BigEndian(input.AsSpan(16), 65);
            await sender.WriteAsync(heartbeat, timeout.Token).ConfigureAwait(true);
            await sender.WriteAsync(input, timeout.Token).ConfigureAwait(true);
            byte[] received = new byte[24];
            await reader.ReadExactlyAsync(received, timeout.Token).ConfigureAwait(true);
            Assert.Equal(input, received);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bridge.WaitAsync(TimeSpan.FromSeconds(8), timeout.Token)).ConfigureAwait(true);
            Assert.False(timeout.IsCancellationRequested);
        }
        finally
        {
            await timeout.CancelAsync().ConfigureAwait(true);
            try
            {
                await bridge.ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                // timeout時にも両方向の完了を待ってからstreamを破棄します。
            }
        }
    }
}
