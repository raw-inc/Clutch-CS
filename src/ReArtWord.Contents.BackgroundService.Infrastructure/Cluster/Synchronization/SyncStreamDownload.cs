using System.Net;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.Runtime.Networking;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;

/// <summary>固定NICから確認済みPeerの同期本文を読み、接続不能時だけ従来経路へ戻します。</summary>
internal static class SyncStreamDownload
{
    /// <summary>新方式の接続確認後は、一つの固定本文を有限の待機で受信します。</summary>
    /// <returns>接続できた場合true、未対応または接続前の不達だけfalseです。</returns>
    internal static async Task<bool> CopyAsync(SyncManifest manifest, LocalNetworkBinding binding, string address, Stream destination,
        TimeSpan idleTimeout, CancellationToken cancellationToken = default)
    {
        if (manifest.Stream is not { } ticket)
        {
            return false;
        }
        using NetworkBindingLease lease = new(binding, cancellationToken);
        using TcpClient client = new(new IPEndPoint(binding.Resolve(), 0));
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(lease.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await client.ConnectAsync(IPAddress.Parse(address), ticket.Port, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is SocketException or OperationCanceledException)
        {
            lease.Token.ThrowIfCancellationRequested();
            return false;
        }
        NetworkStream stream = client.GetStream();
        byte[] header = new byte[56];
        "RAWSYN01"u8.CopyTo(header);
        manifest.Id.TryWriteBytes(header.AsSpan(8, 16));
        manifest.TargetInstanceId.TryWriteBytes(header.AsSpan(24, 16));
        ticket.Token.TryWriteBytes(header.AsSpan(40, 16));
        await stream.WriteAsync(header, deadline.Token).ConfigureAwait(false);
        await stream.ReadExactlyAsync(header.AsMemory(0, 1), deadline.Token).ConfigureAwait(false);
        if (header[0] != 1)
        {
            throw new SyncFailureException("Sync.SessionUnavailable");
        }
        byte[] buffer = new byte[65536];
        long remaining = manifest.Length;
        while (0 < remaining)
        {
            deadline.CancelAfter(idleTimeout);
            int count = await stream.ReadAsync(buffer.AsMemory(0, checked((int)Math.Min(buffer.Length, remaining))), deadline.Token).ConfigureAwait(false);
            if (count == 0)
            {
                throw new SyncFailureException("Sync.ChunkMismatch");
            }
            await destination.WriteAsync(buffer.AsMemory(0, count), lease.Token).ConfigureAwait(false);
            remaining -= count;
        }
        deadline.CancelAfter(idleTimeout);
        if (await stream.ReadAsync(buffer.AsMemory(0, 1), deadline.Token).ConfigureAwait(false) != 0)
        {
            throw new SyncFailureException("Sync.ChunkMismatch");
        }
        return true;
    }
}
