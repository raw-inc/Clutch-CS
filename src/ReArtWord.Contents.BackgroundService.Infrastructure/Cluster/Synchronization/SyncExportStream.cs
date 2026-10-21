using System.Net;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Runtime.Networking;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;

/// <summary>一つの固定書出しを、元の相手実体へ期限内だけ連続配送します。</summary>
internal sealed class SyncExportStream : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly CancellationTokenSource stopping;
    private readonly LocalNetworkBinding binding;
    private readonly PeerInfo target;
    private readonly Guid id;
    private readonly string path;
    private readonly Task completion;

    /// <summary>書出しごとの一枠に、固定NICの有限な待受を所有させます。</summary>
    // 抑制理由: 開始Taskをcompletionで所有し、DisposeAsyncで受付停止後に必ずawaitします。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW511", Justification = "所有する受付TaskをDisposeAsyncで回収します。")]
    internal SyncExportStream(LocalNetworkBinding binding, PeerInfo target, Guid id, string path, TimeSpan lifetime, TimeProvider clock)
    {
        this.binding = binding;
        this.target = target;
        this.id = id;
        this.path = path;
        listener = new(binding.Resolve(), 0);
        stopping = new(lifetime, clock);
        bool started = false;
        try
        {
            listener.Start(1);
            started = true;
        }
        finally
        {
            if (!started)
            {
                listener.Dispose();
                stopping.Dispose();
            }
        }
        Ticket = new(((IPEndPoint)listener.LocalEndpoint).Port, Guid.NewGuid());
        completion = RunAsync(stopping.Token);
    }

    /// <summary>他のPeerや別の起動実体へ使い回せない接続情報です。</summary>
    internal SyncStreamTicket Ticket
    {
        get;
    }

    /// <summary>同時接続を一つに限定し、途中切断では固定ファイルを保持します。</summary>
    /// <returns>期限または所有者の終了までの受付です。</returns>
    private async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (client.Client.RemoteEndPoint is IPEndPoint remote && remote.Address.Equals(IPAddress.Parse(target.Address)))
                    {
                        await SendAsync(client, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception error) when (error is IOException or SocketException or OperationCanceledException)
                {
                    // 受信側が完全ハッシュを検証するため、途中のデータを成功として通知しません。
                }
            }
        }
        catch (Exception error) when (error is SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // 待受の終了は、元の同期結果の成功を意味しません。
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>送信元IP、元要求、相手実体、許可を検証してから固定本文を送ります。</summary>
    /// <returns>一接続への読取り専用配送です。</returns>
    private async Task SendAsync(TcpClient client, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshake.CancelAfter(TimeSpan.FromSeconds(2));
        using NetworkStream stream = client.GetStream();
        byte[] header = new byte[56];
        await stream.ReadExactlyAsync(header, handshake.Token).ConfigureAwait(false);
        if (!header.AsSpan(0, 8).SequenceEqual("RAWSYN01"u8) || new Guid(header.AsSpan(8, 16)) != id
            || new Guid(header.AsSpan(24, 16)) != target.InstanceId || new Guid(header.AsSpan(40, 16)) != Ticket.Token)
        {
            return;
        }
        using NetworkBindingLease lease = new(binding, cancellationToken);
        _ = binding.Resolve();
        using FileStream content = new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await stream.WriteAsync(new byte[] { 1 }, handshake.Token).ConfigureAwait(false);
        await content.CopyToAsync(stream, 65536, lease.Token).ConfigureAwait(false);
        await stream.FlushAsync(lease.Token).ConfigureAwait(false);
    }

    /// <summary>受付と進行中の読取りを止め、その終了を観測してから所有資源を解放します。</summary>
    /// <returns>作業枠を再利用できる終了確認です。</returns>
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await stopping.CancelAsync().ConfigureAwait(false);
        listener.Stop();
        await completion.ConfigureAwait(false);
        listener.Dispose();
        stopping.Dispose();
    }
}
