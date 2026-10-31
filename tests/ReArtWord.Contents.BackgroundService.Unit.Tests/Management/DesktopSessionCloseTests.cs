using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>実TCPとpipeで正常切断とフレーム途中の切断の診断を分けます。</summary>
public sealed class DesktopSessionCloseTests
{
    /// <summary>完全な入力後のFINでは警告せず、部分入力後のFINでは診断を保持します。</summary>
    /// <returns>両方向の回収とログの境界です。</returns>
    // 抑制理由: 通信タスクをfinallyで回収してから所有streamを破棄します。
    [Theory, SuppressMessage("Reliability", "CA2025", Justification = "finallyで中継終了を待ちます。")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(23)]
    [InlineData(24)]
    [InlineData(26)]
    public async Task GracefulEndDoesNotLogButPartialInputDoesAsync(int trailingLength)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token);
        using TcpClient accepted = await listener.AcceptTcpClientAsync(timeout.Token);
        using NetworkStream network = accepted.GetStream(), sender = client.GetStream();
        string pipeName = "desktop-close-" + Guid.NewGuid().ToString("N");
        using NamedPipeServerStream worker = new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using NamedPipeClientStream remote = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(worker.WaitForConnectionAsync(timeout.Token), remote.ConnectAsync(timeout.Token)).ConfigureAwait(true);
        SessionTestLog log = new();
        SessionFailureBoundary boundary = new(log, "peer");
        using Task bridge = boundary.RunAsync(() => DesktopSession.BridgeAsync(network, worker, timeout.Token), "RemoteDesktop", Guid.NewGuid(), "connection", timeout.Token);
        try
        {
            byte[] input = new byte[24];
            BinaryPrimitives.WriteInt32BigEndian(input, 4);
            await sender.WriteAsync(input, timeout.Token);
            byte[] received = new byte[input.Length];
            await remote.ReadExactlyAsync(received, timeout.Token);
            Assert.Equal(input, received);
            if (trailingLength != 0)
            {
                byte[] trailing = new byte[trailingLength];
                if (24 <= trailingLength)
                {
                    BinaryPrimitives.WriteInt32BigEndian(trailing, 16);
                    BinaryPrimitives.WriteInt32BigEndian(trailing.AsSpan(4), 7);
                    BinaryPrimitives.WriteInt32BigEndian(trailing.AsSpan(8), 4);
                }
                await sender.WriteAsync(trailing, timeout.Token);
                if (24 <= trailingLength)
                {
                    await remote.ReadExactlyAsync(received, timeout.Token);
                }
            }
            client.Client.Shutdown(SocketShutdown.Send);
            await bridge.WaitAsync(TimeSpan.FromSeconds(3), timeout.Token);
            Assert.False(timeout.IsCancellationRequested);
            if (trailingLength == 0)
            {
                Assert.Empty(log.Entries);
            }
            else
            {
                Assert.Equal("EndOfStreamException", Assert.Single(log.Entries).Data!.Json.GetProperty("exceptionType").GetString());
            }
        }
        finally
        {
            await timeout.CancelAsync();
            await bridge.ConfigureAwait(true);
        }
    }
}
