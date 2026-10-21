using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;

/// <summary>Core Peerとして参加せず、選択NIC上のクラスタ発見と管理操作を行います。</summary>
public sealed class UdpObserver : IAsyncDisposable
{
    private readonly ObserverOptions options;
    private readonly UdpTransport transport;
    private readonly ObserverCatalog catalog;
    private readonly TimeProvider clock;
    private readonly string senderId;
    private readonly Guid instanceId = Guid.NewGuid();
    private bool started;
    private bool disposed;

    /// <summary>クライアント自身の明示NICとブロードキャスト範囲を固定します。</summary>
    public UdpObserver(ObserverOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Binding);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ObserverId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Binding.InterfaceId);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Binding.Port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.Binding.Port, 65535);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.DiscoveryLifetime, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.RequestTimeout, TimeSpan.Zero);
        this.options = options;
        clock = timeProvider ?? TimeProvider.System;
        senderId = "observer:" + options.ObserverId;
        catalog = new(clock, options.DiscoveryLifetime);
        transport = new UdpTransport(new DatagramTransportOptions(senderId, options.Binding.BindAddress, options.Binding.BroadcastAddress, options.Binding.Port)
        { RequestTimeout = options.RequestTimeout, MaximumMessageBytes = options.MaximumMessageBytes }, instanceId, clock) { Received = ReceiveAsync };
        transport.Initialize(options.Binding.InterfaceId);
    }

    /// <summary>発見パケットを受信します。Peer announcementやsyncは送信しません。</summary>
    /// <returns>受動監視開始です。</returns>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!started)
        {
            transport.Start();
            started = true;
        }
        return Task.CompletedTask;
    }

    /// <summary>クライアント自身のNIC候補と実通信成立を返します。</summary>
    /// <returns>選択と利用可能性を分けた状態です。</returns>
    public ClientNetworkState GetNetworkState()
    {
        ClientNetworkStatus status = CaptureBindingStatus();
        return new(status.Binding, ObserverNetwork.GetInterfaces(), status.Listening, status.Problem);
    }

    /// <summary>他NICの列挙を行わず、この監視の成立条件だけを確認します。</summary>
    /// <returns>固定割当の現在状態です。</returns>
    public ClientNetworkStatus CaptureBindingStatus()
    {
        bool available = started && transport.BindingAvailable;
        string? problem = null;
        if (!available)
        {
            problem = "Observer.BindingUnavailable";
        }
        return new(options.Binding, available, problem);
    }

    /// <summary>受動受信した存在情報を返します。</summary>
    /// <returns>TTL内のクラスタ一覧です。</returns>
    public IReadOnlyList<DiscoveredCluster> GetClusters() => catalog.Snapshot();

    /// <summary>開始済みの監視から現在の発見結果を取得します。</summary>
    /// <returns>実受信したクラスタ一覧です。</returns>
    public ValueTask<IReadOnlyList<DiscoveredCluster>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(GetClusters());
    }

    /// <summary>副作用を登録できないCore照会経路へ送ります。</summary>
    /// <returns>現在の読取結果です。</returns>
    public Task<ManagementReply> InspectAsync(ManagementCommand command, CancellationToken cancellationToken = default) => SendAsync(command, true, false, cancellationToken);

    /// <summary>参照専用の欠測だけを最大3回照会し、対象実体・元要求・期限を維持します。</summary>
    /// <returns>確認結果、または有限回の照会後の欠測です。</returns>
    public async Task<ManagementReply> InspectWithRetryAsync(ManagementCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        string unconfirmed = "Observer.ResponseUnconfirmed";
        ManagementReply reply = new(EOutcome.Unknown, Reason: unconfirmed);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            reply = await InspectAsync(command, cancellationToken).ConfigureAwait(false);
            if (attempt == 2 || reply.Outcome != EOutcome.Unknown || !string.Equals(reply.Reason, unconfirmed, StringComparison.Ordinal)
                || command.ExpiresAt <= clock.GetUtcNow())
            {
                return reply;
            }
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        return reply;
    }

    /// <summary>指定された元IDと実体へ一度配送し、別IDや別実体に差し替えません。</summary>
    /// <returns>管理結果または確認不能です。</returns>
    public Task<ManagementReply> ExecuteAsync(ManagementCommand command, CancellationToken cancellationToken = default) => SendAsync(command, false, false, cancellationToken);

    /// <summary>登録された短命UI状態だけを非永続通知し、処理結果を受け取ります。</summary>
    /// <returns>通知先の受付結果です。</returns>
    public Task<ManagementReply> NotifyAsync(ManagementCommand command, CancellationToken cancellationToken = default) => SendAsync(command, false, true, cancellationToken);

    /// <summary>元要求の指紋を変えず、新実体でも既存操作記録だけを読みます。</summary>
    /// <returns>実行中・完了・確認不能の区別です。</returns>
    public Task<ManagementQueryResult> QueryAsync(ManagementCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return QueryAsync(new ManagementReceiptQuery(command.RequestId, command.ClusterId, command.TargetPeerId, ContractJson.Hash(ManagementWire.Request(command, false))), cancellationToken);
    }

    /// <summary>秘密ペイロードなしで元操作記録を読み、新実体へ副作用を再送しません。</summary>
    /// <returns>元操作の実行中・完了・確認不能です。</returns>
    public async Task<ManagementQueryResult> QueryAsync(ManagementReceiptQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        PeerInfo? current = FindPeers(query.ClusterId, query.TargetPeerId).OrderByDescending(peer => peer.LastSeen).FirstOrDefault();
        if (current is null || !started || !transport.BindingAvailable)
        {
            return new(false, Reason: "Observer.ReceiptTargetUnavailable");
        }
        RequestQuery receipt = new(query.RequestId, query.Fingerprint);
        WireMessage? response = await ExchangeAsync(query.ClusterId, query.TargetPeerId, current.InstanceId, "receipt.query", receipt, cancellationToken).ConfigureAwait(false);
        return ManagementWire.QueryReply(response);
    }

    /// <summary>新規開始の条件を確認し、元要求を共通輸送へ送ります。</summary>
    /// <returns>元の管理応答です。</returns>
    private async Task<ManagementReply> SendAsync(ManagementCommand command, bool readOnly, bool transient, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.RequestId == Guid.Empty || command.ExpiresAt <= clock.GetUtcNow() || string.IsNullOrWhiteSpace(command.Operation)
            || !started || !transport.BindingAvailable || !FindPeers(command.ClusterId, command.TargetPeerId).Any(peer => peer.InstanceId == command.TargetInstanceId))
        {
            return new(EOutcome.NotExecuted, Reason: "Observer.OriginalTargetOrRequestUnavailable");
        }
        ClusterRequest request = ManagementWire.Request(ContractJson.Copy(command), readOnly, transient);
        string kind = "request";
        if (readOnly || transient)
        {
            kind = "inspect";
        }
        WireMessage? response = await ExchangeAsync(command.ClusterId, command.TargetPeerId, command.TargetInstanceId, kind, request, cancellationToken).ConfigureAwait(false);
        return ManagementWire.Response(response);
    }

    /// <summary>クラスタと論理Peerを明示して対象を検索します。</summary>
    /// <returns>観測した元または再起動後の実体です。</returns>
    private IEnumerable<PeerInfo> FindPeers(string clusterId, string peerId) => catalog.Snapshot().Where(cluster => string.Equals(cluster.ClusterId, clusterId, StringComparison.Ordinal))
        .SelectMany(cluster => cluster.Peers).Where(peer => string.Equals(peer.PeerId, peerId, StringComparison.Ordinal));

    /// <summary>輸送不能を副作用再発行へ変換しません。</summary>
    /// <returns>確認した同クラスタ・対象実体の応答です。</returns>
    private async Task<WireMessage?> ExchangeAsync<T>(string clusterId, string peerId, Guid targetInstance, string kind, T body, CancellationToken cancellationToken = default)
    {
        try
        {
            WireMessage request = new(Guid.NewGuid(), clusterId, senderId, instanceId, kind, peerId, null, JsonSerializer.SerializeToElement(body), targetInstance);
            return await transport.RequestAsync(request, targetInstance, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SocketException or IOException or ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>存在情報だけを記録し、同期やPeer参加要求は行いません。</summary>
    /// <returns>受信処理の完了です。</returns>
    private Task ReceiveAsync(WireMessage message, string address, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string hello = "hello";
        if (string.Equals(message.Kind, hello, StringComparison.Ordinal)
            || (options.IncludeDiscoveryAnnouncements && string.Equals(message.Kind, DiscoveryProtocol.AnnouncementKind, StringComparison.Ordinal)))
        {
            PeerAnnouncement? announcement = message.Body.Deserialize<PeerAnnouncement>(ContractJson.Options);
            if (announcement is not null)
            {
                catalog.Observe(message, announcement, address);
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>監視処理の通信資源だけを終了します。</summary>
    /// <returns>終了完了です。</returns>
    ValueTask IAsyncDisposable.DisposeAsync()
    {
        disposed = true;
        started = false;
        return ((IAsyncDisposable)transport).DisposeAsync();
    }
}
