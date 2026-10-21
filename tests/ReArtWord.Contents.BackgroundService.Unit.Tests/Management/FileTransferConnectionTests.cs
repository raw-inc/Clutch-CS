using System.Buffers.Binary;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.CoreService.Files;
using ReArtWord.Contents.BackgroundService.CoreService.Files.Transfers;
using ReArtWord.Contents.BackgroundService.CoreService.Management;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Files;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;
using ReArtWord.Contents.BackgroundService.Runtime.Networking;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>TCPへの切替で接続許可、実体固定、確定の永続受付を迂回させません。</summary>
public sealed class FileTransferConnectionTests
{
    /// <summary>秘密、対象、操作またはフレームが不正なら、handlerを起動せず閉じます。</summary>
    /// <returns>実TCPと実sessionの拒否境界です。</returns>
    // 抑制理由: sessionは直後のawait usingで所有し、通信と共に回収します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW141", Justification = "sessionを直後のawait usingへ渡します。")]
    [Theory]
    [InlineData("token")]
    [InlineData("instance")]
    [InlineData("commit")]
    [InlineData("oversized")]
    [InlineData("truncated")]
    public async Task InvalidConnectionsCannotExecuteFileOperations(string invalid)
    {
        string address = "127.0.0.1";
        string nic = ObserverNetwork.GetInterfaces().Single(item => item.Addresses.Any(value => string.Equals(value.Address, address, StringComparison.Ordinal))).InterfaceId;
        FileTransferPeer peer = new("cluster", "peer", Guid.NewGuid());
        ManagementRegistry registry = new();
        int calls = 0;
        registry.ReadValue("transfer.stat", _ =>
        {
            Interlocked.Increment(ref calls);
            return true;
        });
        registry.Write("transfer.commit", (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<object?>(true);
        });
        FileTransferSession session = new(new LocalNetworkBinding(nic, address), peer, registry);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = session.ConfigureAwait(true);
        using TcpClient client = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        await client.ConnectAsync(address, session.Ticket.Port, timeout.Token).ConfigureAwait(true);
        using NetworkStream stream = client.GetStream();
        byte[] secret = Convert.FromHexString(session.Ticket.Token);
        string tokenCase = "token";
        if (string.Equals(invalid, tokenCase, StringComparison.Ordinal))
        {
            secret[0] ^= 1;
        }
        await stream.WriteAsync(secret, timeout.Token).ConfigureAwait(true);
        byte[] accepted = new byte[1];
        if (!string.Equals(invalid, tokenCase, StringComparison.Ordinal))
        {
            await stream.ReadExactlyAsync(accepted, timeout.Token).ConfigureAwait(true);
            Assert.Equal(1, accepted[0]);
            switch (invalid)
            {
                case "oversized":
                case "truncated":
                    byte[] header = new byte[4];
                    int length = 3 * 1024 * 1024 + 1;
                    string truncated = "truncated";
                    if (string.Equals(invalid, truncated, StringComparison.Ordinal))
                    {
                        length = 4096;
                    }
                    BinaryPrimitives.WriteInt32BigEndian(header, length);
                    await stream.WriteAsync(header, timeout.Token).ConfigureAwait(true);
                    client.Client.Shutdown(SocketShutdown.Send);
                    break;
                default:
                    Guid instance = peer.InstanceId;
                    string operation = "transfer.stat", changedInstance = "instance";
                    if (string.Equals(invalid, changedInstance, StringComparison.Ordinal))
                    {
                        instance = Guid.NewGuid();
                    }
                    else
                    {
                        operation = "transfer.commit";
                    }
                    ManagementCommand command = new(Guid.NewGuid(), peer.ClusterId, peer.PeerId, instance, DateTimeOffset.UtcNow.AddSeconds(3), operation, null);
                    await FileTransferWire.WriteAsync(stream, command, timeout.Token).ConfigureAwait(true);
                    break;
            }
        }
        await session.Completion.WaitAsync(timeout.Token).ConfigureAwait(true);
        Assert.Equal(0, await stream.ReadAsync(accepted, timeout.Token).ConfigureAwait(true));
        Assert.Equal(0, calls);
    }
}
