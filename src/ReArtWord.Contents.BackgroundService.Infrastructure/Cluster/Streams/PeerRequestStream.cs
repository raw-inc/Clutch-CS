using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.Runtime.Networking;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Streams;

// LEGACY-FORMAT: peer-stream-v2 — RAWREQ02受付は全PeerのRAWREQ03対応確認後に撤去します。
/// <summary>有限数のTCP要求を、UDPと同じ一回性・保存確認の受付へ接続します。</summary>
internal sealed class PeerRequestStream : IAsyncDisposable
{
    private readonly PeerCluster cluster;
    private readonly Func<LocalNetworkBinding> binding;
    private readonly Func<WireMessage, CancellationToken, Task<JsonElement>> receive;
    private readonly int maximum;
    private readonly PeerStreamClient client;
    private readonly CancellationTokenSource stopping = new();
    private readonly SemaphoreSlim messages = new(16, 16);
    private readonly SemaphoreSlim controls = new(8, 8);
    private readonly SemaphoreSlim confirmations = new(8, 8);
    private TcpListener? listener;
    private Task completion = Task.CompletedTask;
    private PeerStreamTicket? ticket;
    private long received;

    /// <summary>対応照会だけを従来経路へ公開し、受信本文は同じ処理境界へ渡します。</summary>
    internal PeerRequestStream(PeerCluster cluster, Func<LocalNetworkBinding> binding, ClusterOptions options,
        Func<WireMessage, CancellationToken, Task<JsonElement>> receive, Func<WireMessage, CancellationToken, Task<WireMessage?>> datagram)
    {
        this.cluster = cluster;
        this.binding = binding;
        maximum = options.MaximumMessageBytes;
        this.receive = receive;
        client = new(cluster, binding, datagram, options.RequestTimeout, maximum, options.PreferRequestStream, stopping.Token);
        cluster.RegisterInspection(PeerStreamTicket.Capability, (_, _) => Task.FromResult(new ClusterResponse(EOutcome.Succeeded,
            NodeValue.FromObject(Volatile.Read(ref ticket)))));
    }

    /// <summary>本文を検証して共通受付へ渡した件数です。</summary>
    internal long ReceivedCount => Interlocked.Read(ref received);

    /// <summary>同じNICを使って、固定相手へ一度の配送を行います。</summary>
    /// <returns>対応している経路で確認した応答です。</returns>
    internal Task<WireMessage?> ExchangeAsync(WireMessage message, CancellationToken cancellationToken = default) => client.ExchangeAsync(message, cancellationToken);

    /// <summary>起動中は同じ受付ポートを維持し、確保できなければ従来UDPを使用します。</summary>
    // 抑制理由: 受付タスクをcompletionで所有し、StopAsyncと非同期破棄で必ず待機します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW511", Justification = "長寿命受付をcompletionへ保存し、停止と破棄で終了を待ちます。")]
    internal void Start()
    {
        try
        {
            listener = new(binding().Resolve(), 0);
            listener.Start(256);
            Volatile.Write(ref ticket, new(((IPEndPoint)listener.LocalEndpoint).Port, Guid.NewGuid(), cluster.LocalInstanceId, maximum) { PrioritizedFrames = true });
            completion = RunAsync(listener, stopping.Token);
        }
        catch (Exception error) when (error is SocketException or IOException or InvalidOperationException)
        {
            using TcpListener? failed = listener;
            listener = null;
        }
    }

    /// <summary>接続と所有タスクを256件以内に保ち、停止前に全件の終了を確認します。</summary>
    /// <returns>受付の寿命です。</returns>
    // 抑制理由: 受理した接続をReceiveOwnedAsyncへ移し、そのusing範囲で破棄します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW141", Justification = "受理した接続の所有権をReceiveOwnedAsyncへ渡します。")]
    private async Task RunAsync(TcpListener server, CancellationToken cancellationToken = default)
    {
        List<Task> pending = [];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                for (int index = pending.Count - 1; 0 <= index; index--)
                {
                    if (pending[index].IsCompleted)
                    {
                        await pending[index].ConfigureAwait(false);
                        pending.RemoveAt(index);
                    }
                }
                if (256 <= pending.Count)
                {
                    await Task.WhenAny(pending).WaitAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }
                TcpClient client = await server.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                pending.Add(ReceiveOwnedAsync(client, cancellationToken));
            }
        }
        catch (Exception error) when (error is SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // 既に共通受付へ渡した副作用は、その元操作の記録から確認します。
        }
        finally
        {
            Volatile.Write(ref ticket, null);
            server.Stop();
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
    }

    /// <summary>接続の所有権を引き取り、受信失敗も必ずソケット解放へ収束させます。</summary>
    /// <returns>一接続の終了確認です。</returns>
    private async Task ReceiveOwnedAsync(TcpClient client, CancellationToken cancellationToken = default)
    {
        using (client)
        {
            try
            {
                await ReceiveAsync(client, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // 接続失敗を完了ACKにせず、送信側は元の要求だけを照会します。
            }
        }
    }

    /// <summary>相手実体・送信元IP・接続許可を検証してから有限本文を受け取ります。</summary>
    /// <returns>元の要求IDに対応する一つの応答です。</returns>
    // 抑制理由: capacityは所有者が停止後に破棄する共有Semaphoreの借用です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW141", Justification = "受付枠は共有資源であり、停止後のDisposeAsyncが破棄します。")]
    private async Task ReceiveAsync(TcpClient client, CancellationToken cancellationToken = default)
    {
        PeerStreamTicket? current = Volatile.Read(ref ticket);
        if (current is null || client.Client.RemoteEndPoint is not IPEndPoint remote)
        {
            return;
        }
        client.NoDelay = true;
        using NetworkStream stream = client.GetStream();
        using NetworkBindingLease lease = new(binding(), cancellationToken);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(lease.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        byte[] header = new byte[56];
        await stream.ReadExactlyAsync(header, deadline.Token).ConfigureAwait(false);
        Guid sender = new(header.AsSpan(24, 16));
        PeerInfo? peer = cluster.GetPeers().FirstOrDefault(candidate => candidate.InstanceId == sender && remote.Address.Equals(IPAddress.Parse(candidate.Address)));
        bool prioritized = header.AsSpan(0, 8).SequenceEqual("RAWREQ03"u8);
        if ((!prioritized && !header.AsSpan(0, 8).SequenceEqual("RAWREQ02"u8)) || new Guid(header.AsSpan(8, 16)) != current.InstanceId
            || new Guid(header.AsSpan(40, 16)) != current.Token || peer is null)
        {
            return;
        }
        _ = binding().Resolve(true);
        await stream.WriteAsync(new byte[] { 1 }, deadline.Token).ConfigureAwait(false);
        byte[] prefix = new byte[4];
        while (!cancellationToken.IsCancellationRequested)
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            int first = await stream.ReadAsync(prefix.AsMemory(0, 1), deadline.Token).ConfigureAwait(false);
            if (first == 0)
            {
                return;
            }
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            await stream.ReadExactlyAsync(prefix.AsMemory(1), deadline.Token).ConfigureAwait(false);
            int encoded = BinaryPrimitives.ReadInt32BigEndian(prefix);
            int lane = prioritized ? (int)((uint)encoded >> 30)
                                   : 0;
            int length = prioritized ? encoded & 0x3fffffff
                                     : encoded;
            if (length <= 0 || maximum < length || lane == 3)
            {
                return;
            }
            SemaphoreSlim capacity = lane switch { 1 => controls, 2 => confirmations, _ => messages };
            await capacity.WaitAsync(deadline.Token).ConfigureAwait(false);
            try
            {
                byte[] bytes = new byte[length];
                await stream.ReadExactlyAsync(bytes, deadline.Token).ConfigureAwait(false);
                WireMessage? message = JsonSerializer.Deserialize<WireMessage>(bytes);
                if (message is null || message.Id == Guid.Empty || message.ReplyTo is not null || message.InstanceId != sender
                    || message.TargetInstanceId != current.InstanceId || !string.Equals(message.ClusterId, cluster.LocalClusterId, StringComparison.Ordinal)
                    || !string.Equals(message.TargetId, cluster.LocalPeerId, StringComparison.Ordinal) || !string.Equals(message.SenderId, peer.PeerId, StringComparison.Ordinal)
                    || !cluster.GetPeers().Any(known => known.InstanceId == sender && string.Equals(known.PeerId, peer.PeerId, StringComparison.Ordinal))
                    || !PeerStreamTicket.Handles(message.Kind) || prioritized && lane != PeerStreamFrame.Lane(message.Kind))
                {
                    return;
                }
                deadline.Token.ThrowIfCancellationRequested();
                deadline.CancelAfter(Timeout.InfiniteTimeSpan);
                cluster.ObserveTraffic(message.SenderId, message.InstanceId);
                Interlocked.Increment(ref received);
                JsonElement result = await receive(message, cancellationToken).ConfigureAwait(false);
                byte[] reply = JsonSerializer.SerializeToUtf8Bytes(WireMessage.Reply(message, cluster.LocalPeerId, cluster.LocalInstanceId, result));
                if (maximum < reply.Length)
                {
                    return;
                }
                using CancellationTokenSource responseDeadline = CancellationTokenSource.CreateLinkedTokenSource(lease.Token);
                responseDeadline.CancelAfter(TimeSpan.FromSeconds(10));
                await TcpFrame.WriteAsync(stream, reply, "lengthPrefix", responseDeadline.Token).ConfigureAwait(false);
            }
            finally
            {
                capacity.Release();
            }
        }
    }

    /// <summary>受付と受信を停止し、所有する要求タスクの終了後に通信資源を解放します。</summary>
    /// <returns>受付の停止確認です。</returns>
    internal async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await stopping.CancelAsync().ConfigureAwait(false);
        listener?.Stop();
        await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>通常停止後も二重解放せず残る所有資源を破棄します。</summary>
    /// <returns>通信資源の解放です。</returns>
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await ((IAsyncDisposable)client).DisposeAsync().ConfigureAwait(false);
        listener?.Dispose();
        messages.Dispose();
        controls.Dispose();
        confirmations.Dispose();
        stopping.Dispose();
    }
}
