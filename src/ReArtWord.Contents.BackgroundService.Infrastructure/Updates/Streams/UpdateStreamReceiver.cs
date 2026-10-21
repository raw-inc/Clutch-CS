using System.Net;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Local;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Streams;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Runtime.Networking;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Streams;

/// <summary>固定NIC上の一つのTCP転送をHost寿命内で所有します。</summary>
public sealed class UpdateStreamReceiver
{
    private readonly Func<UpdatePeerAgent> agent;
    private readonly Func<UpdatesOptions> options;
    private readonly Func<string, string?> sourceAddress;
    private readonly Guid instanceId;
    private readonly Lock gate = new();
    private UpdateStreamSession? session;
    private int port;

    /// <summary>bootstrapで確定する設定と、実観測した送信元を接続します。</summary>
    public UpdateStreamReceiver(Func<UpdatePeerAgent> agent, Func<UpdatesOptions> options, Func<string, string?> sourceAddress, Guid instanceId)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sourceAddress);
        this.agent = agent;
        this.options = options;
        this.sourceAddress = sourceAddress;
        this.instanceId = instanceId;
    }

    /// <summary>現在待受している場合だけ新しい転送方式を通知します。</summary>
    public UpdateStreamCapabilities Capabilities
    {
        get
        {
            int current = Volatile.Read(ref port);
            int version = 0;
            if (current != 0)
            {
                version = 1;
            }
            return new(version, current, UpdateStreamPayload.BufferBytes);
        }
    }

    /// <summary>一回の固定ジョブへ接続を許可し、別要求や再起動による再送を拒否します。</summary>
    /// <returns>短命で推測不能なトークンです。</returns>
    public Task<UpdateStreamTicket> OpenAsync(UpdateStreamRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string source = sourceAddress(request.SourcePeerId) ?? throw new InvalidOperationException("送信元Peerを確認できません。");
        return OpenAsync(request.OperationId, source, cancellationToken);
    }

    /// <summary>管理要求の実送信元へ、一回限りのTCP転送を許可します。</summary>
    /// <returns>固定ジョブと送信元IPに対応する短命のトークンです。</returns>
    public async Task<UpdateStreamTicket> OpenAsync(Guid operationId, string observedAddress, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(observedAddress);
        IPAddress source = IPAddress.Parse(observedAddress);
        LocalUpdateJob job = await agent().ReadJobAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (job.PreviousInstanceId != instanceId || Volatile.Read(ref port) == 0)
        {
            throw new InvalidOperationException("転送先の実体または待受が変更されています。");
        }
        _ = new LocalNetworkBinding(job.Options.InterfaceId!, job.Options.LocalAddress!).Resolve();
        UpdateLayout layout = new(job.Options, job.OperationId);
        string claim = Path.Combine(layout.OperationRoot, "stream-reserved");
        layout.Validate(claim);
        UpdateStreamSession created = new(job.OperationId, source);
        using (gate.EnterScope())
        {
            if (session is not null && !session.CanReplace)
            {
                throw new InvalidOperationException("別の転送が予約または進行中です。");
            }

            // 接続前の一度だけの予約です。部分転送を別トークンで暗黙にやり直しません。
            using FileStream marker = new(claim, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1, FileOptions.WriteThrough);
            marker.Write(created.Ticket.Token.ToByteArray());
            session = created;
        }
        return created.Ticket;
    }

    /// <summary>接続応答が消失しても、同じ転送の状態だけを照会します。</summary>
    /// <returns>現在の有限状態、または保持対象外である明示結果です。</returns>
    public UpdateStreamStatus Read(Guid token)
    {
        using (gate.EnterScope())
        {
            if (session?.Ticket.Token == token)
            {
                return session.Read();
            }
            return new(token, 0, true, "update.streamUnknown");
        }
    }

    /// <summary>待受と一件処理を順番にawaitし、背景に未追跡の作業を残しません。</summary>
    /// <returns>Host停止までの待受です。</returns>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UpdatesOptions selected = options();
            if (selected.InterfaceId is not null && selected.LocalAddress is not null)
            {
                try
                {
                    LocalNetworkBinding binding = new(selected.InterfaceId, selected.LocalAddress);
                    using TcpListener listener = new(binding.Resolve(), 0);
                    listener.Start(4);
                    Volatile.Write(ref port, ((IPEndPoint)listener.LocalEndpoint).Port);
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        using TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                        await ReceiveAsync(client, binding, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception exception) when (exception is SocketException or IOException or OperationCanceledException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                finally
                {
                    Volatile.Write(ref port, 0);
                    using (gate.EnterScope())
                    {
                        session?.Complete("update.streamListenerStopped");
                    }
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>小さなhandshakeの確認後だけ、固定マニフェストのバイト列を受け付けます。</summary>
    /// <returns>接続の終了です。失敗を同じ転送状態へ残します。</returns>
    private async Task ReceiveAsync(TcpClient client, LocalNetworkBinding binding, CancellationToken cancellationToken = default)
    {
        UpdateStreamSession? active = null;
        try
        {
            using NetworkBindingLease lease = new(binding, cancellationToken);
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(lease.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            NetworkStream stream = client.GetStream();
            byte[] header = new byte[24];
            await stream.ReadExactlyAsync(header, deadline.Token).ConfigureAwait(false);
            if (!header.AsSpan(0, 8).SequenceEqual("RAWUPD01"u8))
            {
                return;
            }
            Guid token = new(header.AsSpan(8));
            using (gate.EnterScope())
            {
                if (session?.Ticket.Token != token || client.Client.RemoteEndPoint is not IPEndPoint remote || !session.Source.Equals(remote.Address) || !session.TryStart())
                {
                    return;
                }
                active = session;
            }
            deadline.CancelAfter(TimeSpan.FromMinutes(15));
            LocalUpdateJob job = await agent().ReadJobAsync(active.OperationId, deadline.Token).ConfigureAwait(false);
            if (job.PreviousInstanceId != instanceId)
            {
                throw new InvalidDataException("転送先の準備実体が異なります。");
            }
            header[0] = 1;
            await stream.WriteAsync(header.AsMemory(0, 1), deadline.Token).ConfigureAwait(false);
            await UpdateStreamPayload.ReceiveAsync(job, token, stream, active.Add, deadline.Token).ConfigureAwait(false);
            active.Complete(null);
            await stream.WriteAsync(header.AsMemory(0, 1), deadline.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or SocketException or OperationCanceledException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (active is not null && !active.Read().Completed)
            {
                active.Complete("update.streamInterrupted");
            }
        }
    }
}
