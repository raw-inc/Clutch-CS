using System.Net;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Streams;
using ReArtWord.Contents.BackgroundService.Runtime.Networking;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Streams;

/// <summary>既存管理経路で許可された一回の連続転送を行います。</summary>
public static class UpdateStreamSender
{
    /// <summary>転送予約前に接続だけを検査し、ポート未許可なら従来方式を使用可能にします。</summary>
    /// <returns>ペイロードを一切送らず接続できたかです。</returns>
    public static async Task<bool> CanConnectAsync(ClientNetworkBinding binding, string address, int port, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using TcpClient client = new(new IPEndPoint(new LocalNetworkBinding(binding.InterfaceId, binding.BindAddress).Resolve(), 0));
            await client.ConnectAsync(IPAddress.Parse(address), port, deadline.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }

    /// <summary>固定NICへbindし、一つの接続と再利用バッファーで送信します。</summary>
    /// <returns>受信側が全内容の検証と保存を終えた確認です。</returns>
    public static Task SendAsync(ClusterUpdateRequest request, ClientNetworkBinding binding, string address, int port, UpdateStreamTicket ticket, CancellationToken cancellationToken = default)
        => SendAsync(request, binding, address, port, ticket, null, cancellationToken);

    /// <summary>送信済みバイト数を通知し、最後に受信側の保存結果を確認します。</summary>
    /// <returns>全内容の検証と保存の確認です。</returns>
    public static async Task SendAsync(ClusterUpdateRequest request, ClientNetworkBinding binding, string address, int port, UpdateStreamTicket ticket, Action<long, long>? progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        LocalNetworkBinding network = new(binding.InterfaceId, binding.BindAddress);
        using NetworkBindingLease lease = new(network, cancellationToken);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(lease.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using TcpClient client = new(new IPEndPoint(network.Resolve(), 0));
        await client.ConnectAsync(IPAddress.Parse(address), port, deadline.Token).ConfigureAwait(false);
        NetworkStream stream = client.GetStream();
        byte[] header = new byte[24];
        "RAWUPD01"u8.CopyTo(header);
        ticket.Token.TryWriteBytes(header.AsSpan(8));
        await stream.WriteAsync(header, deadline.Token).ConfigureAwait(false);
        await stream.ReadExactlyAsync(header.AsMemory(0, 1), deadline.Token).ConfigureAwait(false);
        if (header[0] != 1)
        {
            throw new IOException("更新転送の接続許可を確認できません。");
        }
        deadline.CancelAfter(TimeSpan.FromMinutes(15));
        await UpdateStreamPayload.SendAsync(request.Manifest, request.PayloadRoot, stream, progress, deadline.Token).ConfigureAwait(false);
        client.Client.Shutdown(SocketShutdown.Send);
        await stream.ReadExactlyAsync(header.AsMemory(0, 1), deadline.Token).ConfigureAwait(false);
        if (header[0] != 1)
        {
            throw new IOException("更新転送の最終保存を確認できません。");
        }
    }
}
