using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Membership;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Replication;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Requests;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Streams;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.Runtime.Cluster;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;

/// <summary>実状態同期、明示対象配送と参加管理を接続します。</summary>
public sealed class PeerCluster : IPeerDirectory, IRemoteNodeDispatcher, IAsyncDisposable
{
    private readonly ClusterOptions options;
    private readonly IDurableRepository repository;
    private readonly TimeProvider clock;
    private readonly PeerMembership membership;
    private readonly RequestExecutor executor;
    private readonly PeerRuntimeCapabilities runtimeCapabilities;
    private readonly UdpTransport transport;
    private readonly ClusterReplication replication;
    private readonly TransientChannel transient;
    private readonly CancellationTokenSource stopping = new();
    private readonly SyncParticipation participation;
    private readonly Lazy<Task> heartbeat;
    private readonly PeerRecovery recovery;
    private readonly RepositorySynchronization repositorySync;
    private readonly PeerRequestStream requestStream;
    private readonly AsyncLocal<string?> requestAddress = new();

    /// <summary>現在処理している管理要求を実際に受信した送信元です。</summary>
    public string? ManagementSourceAddress => requestAddress.Value;

    /// <summary>必要な通信設定と実保存先を接続します。</summary>
    public PeerCluster(ClusterOptions options, IDurableRepository repository, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(repository);
        RepositorySynchronization.ValidateOptions(options);
        this.options = options;
        this.repository = repository;
        clock = timeProvider ?? TimeProvider.System;
        membership = new PeerMembership(options, Guid.NewGuid(), clock);
        executor = new RequestExecutor(repository, options.PeerId, clock);
        runtimeCapabilities = new(this);
        recovery = new PeerRecovery(options, repository, membership);
        transport = new UdpTransport(new DatagramTransportOptions(options.PeerId, options.BindAddress, options.BroadcastAddress, options.Port, options.Id)
        { RequestTimeout = options.RequestTimeout, MaximumMessageBytes = options.MaximumMessageBytes }, LocalInstanceId, clock) { Received = ReceiveAsync };
        replication = ClusterReplication.Create(this, repository, options.MaximumMessageBytes, clock);
        transient = new TransientChannel(LocalPeerId, LocalInstanceId, (body, token) => transport.SendAsync(CreateMessage("transient", null, body), token), () => recovery.Detached);
        heartbeat = new Lazy<Task>(() => HeartbeatAsync(stopping.Token));
        repositorySync = new(this, repository, options, clock, () => new(transport.InterfaceId, options.BindAddress), stopping.Token);
        participation = new(this, membership, recovery, repositorySync, repository);
        requestStream = new(this, () => new(transport.InterfaceId, options.BindAddress), options,
            (message, token) => IncomingClusterRequests.ReceiveAsync(message.Kind, message.Body, executor, LocalInstanceId,
                (request, inner) => ExecuteLocalAsync(request, false, inner), replication, token),
            (message, token) => transport.RequestAsync(message, message.TargetInstanceId!.Value, token));
    }

    /// <summary>ローカルPeer識別子です。</summary>
    public string LocalPeerId => options.PeerId;

    /// <summary>このPeerが所有する一つのクラスタです。</summary>
    public string LocalClusterId => options.Id;

    /// <summary>共有状態の同期を確認できない最新の理由コードです。</summary>
    public string? SynchronizationProblem => replication.RecoveryProblem ?? repositorySync.Problem;

    /// <summary>Readyを維持したまま行う背景履歴の取得状態です。</summary>
    public HistorySynchronizationStatus? HistorySynchronization => repositorySync.HistoryStatus;

    /// <summary>既存Importが検出した競合を通常参加へ戻しません。</summary>
    internal void MarkSynchronizationConflict() => membership.SetLocal(EPeerStatus.Conflict);

    /// <summary>専用管理受付を構成した場合だけ通常参加外の管理操作を許します。</summary>
    internal bool ManagementRequestsEnabled
    {
        get; set;
    }

    /// <summary>今回の起動実体です。</summary>
    public Guid LocalInstanceId => membership.InstanceId;

    /// <summary>現在通常参加しているCoordinatorです。</summary>
    public string? CoordinatorId => membership.Coordinator;

    /// <summary>実process/resource/interfaceの追加Ready確認を行います。</summary>
    public Func<CancellationToken, ValueTask<bool>>? ReadinessCheck
    {
        get => recovery.ReadinessCheck;
        set => recovery.ReadinessCheck = token => transport.CheckReadyAsync(value, token);
    }

    /// <summary>ノードの具体実行を登録します。</summary>
    public Func<RemoteExecutionRequest, CancellationToken, Task<NodeResult>>? RemoteExecutionHandler
    {
        get => executor.RemoteExecutionHandler;
        set => executor.RemoteExecutionHandler = value;
    }

    /// <summary>実行経路から分離した記録の有限待機量と保存問題です。</summary>
    public object RuntimeStatistics => executor.RuntimeStatistics;

    /// <summary>TCP経路で共通受付へ渡した要求数です。</summary>
    internal long StreamRequestsReceived => requestStream.ReceivedCount;

    /// <summary>配送先実体を検証した通常通信を生存確認へ反映します。</summary>
    internal void ObserveTraffic(string peerId, Guid instanceId) => membership.ObserveTraffic(peerId, instanceId);

    /// <summary>観測したCoordinator交代のローカル世代です。</summary>
    internal long CoordinatorRevision => membership.CoordinatorRevision;

    /// <summary>実行値共有の処理登録後にだけ対応を通知します。</summary>
    internal void EnableBufferedRuntime() => runtimeCapabilities.Enable(membership);

    /// <summary>既にローカル配信した所有事象の遠隔転送です。</summary>
    /// <returns>重複するローカル配信を伴わない一回の送信です。</returns>
    internal Task PublishRemoteTransientAsync(string kind, NodeValue value, CancellationToken cancellationToken = default) => transient.PublishRemoteAsync(kind, value, cancellationToken);

    /// <summary>実行中の値操作を、起動実体内の重複抑止と非同期記録へ接続します。</summary>
    public void RegisterRuntimeHandler(string kind, Func<ClusterRequest, CancellationToken, Task<ClusterResponse>> handler) => executor.RegisterRuntime(kind, handler);

    /// <summary>長い待機を受信接続へ保持せず、元の実行結果だけを呼出側に照会させます。</summary>
    internal void RegisterDeferredRuntimeHandler(string kind, Func<ClusterRequest, CancellationToken, Task<ClusterResponse>> handler) => executor.RegisterRuntime(kind, handler, deferRemoteCompletion: true);

    /// <summary>明示離脱中のローカル呼出しに限って許す処理を判定します。</summary>
    public Func<ClusterRequest, bool>? LocalDetachedExecutionAllowed
    {
        get; set;
    }

    /// <summary>現在の参加情報を取得します。</summary>
    /// <returns>参加一覧。</returns>
    public IReadOnlyList<PeerInfo> GetPeers() => membership.Snapshot();

    /// <summary>共有設定から反映した平らなGroupを取得します。</summary>
    /// <returns>所属対応。</returns>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> GetHostGroups() => membership.GetHostGroups();

    /// <summary>共有済み設定のGroupを反映します。</summary>
    public void UpdateHostGroups(IReadOnlyDictionary<string, IReadOnlyList<string>> groups) => membership.UpdateHostGroups(groups);

    /// <summary>ローカル接続先を更新し、次のハートビートから通知します。</summary>
    public void SetLocalLogPort(int? logPort) => membership.SetLocalLogPort(logPort);

    /// <summary>値を含まない保存状態を次の参加通知へ反映します。</summary>
    public void SetLocalPersistenceStatus(PeerPersistenceStatus status) => membership.SetLocalPersistenceStatus(status);

    /// <summary>管理種類を明示登録します。</summary>
    public void RegisterHandler(string kind, Func<ClusterRequest, CancellationToken, Task<ClusterResponse>> handler) => executor.Register(kind, handler);

    /// <summary>呼出側が元結果を照会する処理だけ、遠隔応答と完了待ちを分離します。</summary>
    internal void RegisterDeferredHandler(string kind, Func<ClusterRequest, CancellationToken, Task<ClusterResponse>> handler) => executor.Register(kind, handler, deferRemoteCompletion: true);

    /// <summary>副作用を持たない照会だけを操作記録非生成の経路へ登録します。</summary>
    public void RegisterInspection(string kind, Func<ClusterRequest, CancellationToken, Task<ClusterResponse>> handler) => executor.RegisterInspection(kind, handler);

    /// <summary>確定保存ではないUI通知を登録します。</summary>
    public void RegisterTransientHandler(string kind, Func<string, Guid, NodeValue, CancellationToken, Task> handler) => transient.Register(kind, handler);

    /// <summary>一時UI状態を永続操作記録なしで一度通知します。</summary>
    /// <returns>送信完了。</returns>
    public Task PublishTransientAsync(string kind, NodeValue value, CancellationToken cancellationToken = default) => transient.PublishAsync(kind, value, cancellationToken);

    /// <summary>保存状態を検査して発見・同期を開始します。</summary>
    /// <returns>起動準備の完了。</returns>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await PeerStorageIdentity.InitializeAsync(repository, options, transport, cancellationToken).ConfigureAwait(false);
        await recovery.InitializeAsync(cancellationToken).ConfigureAwait(false);
        participation.Digest = await repository.GetSharedVersionAsync(cancellationToken).ConfigureAwait(false);
        transport.Start();
        requestStream.Start();
        repositorySync.Start();
        if (heartbeat.Value.IsFaulted)
        {
            throw new InvalidOperationException("参加通知を開始できません。");
        }
    }

    /// <summary>人の明示離脱を再起動後も維持します。</summary>
    /// <returns>保存結果。</returns>
    public Task DetachAsync(CancellationToken cancellationToken = default) => recovery.DetachAsync(cancellationToken);

    /// <summary>再同期を経由して通常参加を要求します。</summary>
    /// <returns>再参加開始。</returns>
    public Task RejoinAsync(CancellationToken cancellationToken = default) => recovery.RejoinAsync(cancellationToken);

    /// <summary>一時的な利用不能とは独立した、明示離脱の保持状態です。</summary>
    public bool IsDetached => recovery.Detached;

    /// <summary>内容競合の依存範囲を隔離したことを通常参加前に確認します。</summary>
    public void SetConflictReadinessCheck(Func<CancellationToken, ValueTask<bool>> check) => recovery.ConflictReadinessCheck = check;

    /// <summary>明示解消後も通常のローカル準備を確認して参加状態を再評価します。</summary>
    /// <returns>参加再確認です。</returns>
    public Task RefreshParticipationAsync(CancellationToken cancellationToken = default) => recovery.BecomeReadyAsync(cancellationToken);

    /// <summary>ノード実行を共通永続操作記録経路へ送ります。</summary>
    /// <returns>実処理または確認不能結果。</returns>
    public Task<NodeResult> ExecuteAsync(RemoteExecutionRequest request, CancellationToken cancellationToken = default) => RemoteNodeCompletion.ExecuteAsync(this, request, cancellationToken);

    /// <summary>要求本体を実行せず、現在の永続結果だけを照会します。</summary>
    /// <returns>現在実行中か、完了結果または確認不能理由。</returns>
    public Task<ClusterRequestStatus> QueryRequestAsync(ClusterRequest request, CancellationToken cancellationToken = default) => RemoteNodeCompletion.QueryAsync(this, executor, request, cancellationToken);

    /// <summary>対象実体へ要求を配送します。</summary>
    /// <returns>確認結果。</returns>
    public Task<ClusterResponse> RequestAsync(ClusterRequest request, CancellationToken cancellationToken = default) => RemoteNodeCompletion.SendAsync(this, request, (command, token) => ExecuteLocalAsync(command, true, token), "request", cancellationToken);

    /// <summary>登録された読取り専用照会を固定実体へ配送します。</summary>
    /// <returns>履歴を作らない照会結果。</returns>
    public Task<ClusterResponse> InspectAsync(ClusterRequest request, CancellationToken cancellationToken = default) => RemoteNodeCompletion.SendAsync(this, request, (command, token) => executor.InspectAsync(command, LocalInstanceId, token), "inspect", cancellationToken);

    /// <summary>固定Ready集合に共有保存します。</summary>
    /// <returns>全対象の永続確認結果。</returns>
    public ValueTask<DurableReceipt> ReplicateAsync(Guid operationId, IReadOnlyList<DurableMutation> mutations, CancellationToken cancellationToken = default) => replication.ReplicateAsync(operationId, mutations, cancellationToken);

    /// <summary>上位操作開始時の固定集合を保存の確認先へ引き継ぎます。</summary>
    /// <returns>全固定対象の永続保存結果です。</returns>
    public ValueTask<DurableReceipt> ReplicateFixedAsync(Guid operationId, IReadOnlyList<DurableMutation> mutations, IReadOnlyList<PeerInfo> members, CancellationToken cancellationToken = default) => replication.ReplicateFixedAsync(operationId, mutations, members, cancellationToken);

    /// <summary>共有操作の結果を照会します。</summary>
    /// <returns>確認済み操作結果。</returns>
    public ValueTask<DurableReceipt?> GetOperationAsync(Guid operationId, CancellationToken cancellationToken = default) => replication.GetOperationAsync(operationId, cancellationToken);

    /// <summary>元の確認対象から永続結果だけを明示再照会します。</summary>
    /// <returns>元操作の再確認結果。</returns>
    public ValueTask<DurableReceipt?> ConfirmReplicationAsync(Guid operationId, IReadOnlyList<PeerInfo> originalMembers, CancellationToken cancellationToken = default) => replication.ConfirmAsync(operationId, originalMembers, cancellationToken);

    /// <summary>保存・実行要求をTCPへ送り、未配送または未対応の場合だけ従来UDPへ戻します。</summary>
    /// <returns>対象実体からの応答。</returns>
    internal Task<WireMessage?> ExchangeAsync(string kind, string peerId, Guid instanceId, JsonElement body, CancellationToken cancellationToken = default)
        => requestStream.ExchangeAsync(CreateMessage(kind, peerId, body) with { TargetInstanceId = instanceId }, cancellationToken);

    /// <summary>配送封筒を構築します。</summary>
    /// <returns>今回のメッセージ。</returns>
    private WireMessage CreateMessage(string kind, string? target, JsonElement body) => new(Guid.NewGuid(), options.Id, LocalPeerId, LocalInstanceId, kind, target, null, body);

    /// <summary>開始前に対象・期限・通常参加を確認します。</summary>
    /// <returns>操作記録を経由した結果。</returns>
    private Task<ClusterResponse> ExecuteLocalAsync(ClusterRequest request, bool localCaller, CancellationToken cancellationToken = default)
        => executor.ExecutePeerAsync(request, GetPeers().Single(peer => peer.InstanceId == LocalInstanceId).Status, LocalInstanceId, localCaller,
            LocalDetachedExecutionAllowed, ManagementRequestsEnabled, cancellationToken);

    /// <summary>発見と実同期・実要求の配送を処理します。</summary>
    /// <returns>受信処理の完了。</returns>
    private async Task ReceiveAsync(WireMessage message, string address, CancellationToken cancellationToken = default)
    {
        string helloKind = "hello";
        string syncKind = "sync.";
        string transientKind = "transient";
        if (string.Equals(message.Kind, transientKind, StringComparison.Ordinal))
        {
            await transient.DispatchAsync(message.SenderId, message.InstanceId, message.Body, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (string.Equals(message.Kind, helloKind, StringComparison.Ordinal))
        {
            PeerInfo? previous = GetPeers().FirstOrDefault(peer => string.Equals(peer.PeerId, message.SenderId, StringComparison.Ordinal));
            PeerAnnouncement? announcement = await runtimeCapabilities.ResolveAsync(message, stopping.Token).ConfigureAwait(false);
            if (announcement is null || !membership.Observe(announcement, address)
                || !await recovery.PersistKnownAsync(cancellationToken).ConfigureAwait(false) || !membership.IsCurrent(announcement))
            {
                return;
            }
            EPeerStatus localStatus = GetPeers().Single(peer => peer.InstanceId == LocalInstanceId).Status;
            bool participating = PeerStatusRules.CanParticipate(localStatus);
            bool conflictsResolved = localStatus == EPeerStatus.Ready && announcement.Peer.Status == EPeerStatus.Ready;
            if (!recovery.Detached && PeerStatusRules.CanParticipate(announcement.Peer.Status) && (!string.Equals(announcement.Digest, participation.Digest, StringComparison.Ordinal) || repositorySync.NeedsSynchronization(announcement.Peer, conflictsResolved) || !participating))
            {
                bool mustGate = previous?.Status == EPeerStatus.Unavailable || !participating;
                await participation.SynchronizeAsync(announcement.Peer, mustGate, conflictsResolved && previous?.InstanceId == announcement.Peer.InstanceId, stopping.Token).ConfigureAwait(false);
            }
            return;
        }
        requestAddress.Value = address;
        try
        {
            JsonElement result = message.Kind.StartsWith(syncKind, StringComparison.Ordinal) ? await repositorySync.ReceiveAsync(message, stopping.Token).ConfigureAwait(false)
                                                                                             : await IncomingClusterRequests.ReceiveAsync(message.Kind, message.Body, executor, LocalInstanceId,
                                                                                                (request, token) => ExecuteLocalAsync(request, false, token), replication, stopping.Token).ConfigureAwait(false);
            await transport.SendAsync(WireMessage.Reply(message, LocalPeerId, LocalInstanceId, result), stopping.Token).ConfigureAwait(false);
        }
        finally
        {
            requestAddress.Value = null;
        }
    }

    /// <summary>期限検査と実状態の通知を続けます。</summary>
    /// <returns>停止までの監視。</returns>
    private Task HeartbeatAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset recoveryAt = clock.GetUtcNow() + options.PeerTimeout;
        return PeerHeartbeat.RunAsync(options.HeartbeatInterval, clock, token => RefreshHeartbeatAsync(recoveryAt, token),
            token => DiscoveryProtocol.AnnounceAsync(transport, CreateMessage("hello", null, JsonSerializer.SerializeToElement(membership.CreateAnnouncement(participation.Digest))), options.Port, token),
            () => membership.SetLocal(EPeerStatus.Unavailable), cancellationToken);
    }

    /// <summary>現在の必要条件と共有版を確認し、失敗時の通知判断は周期処理へ返します。</summary>
    /// <returns>この一巡の状態確認です。</returns>
    private async Task RefreshHeartbeatAsync(DateTimeOffset recoveryAt, CancellationToken cancellationToken = default)
    {
        transport.ValidateBinding();
        membership.Expire();
        await replication.RecoverAsync(cancellationToken).ConfigureAwait(false);
        await repositorySync.CleanupAsync(cancellationToken).ConfigureAwait(false);
        if (recoveryAt <= clock.GetUtcNow())
        {
            await recovery.RecoverAloneAsync(cancellationToken).ConfigureAwait(false);
        }
        await recovery.CheckAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        participation.Digest = await repository.GetSharedVersionAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>通信停止を待ちます。</summary>
    /// <returns>終了確認。</returns>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        membership.SetLocal(EPeerStatus.Unavailable);
        await stopping.CancelAsync().ConfigureAwait(false);
        if (heartbeat.IsValueCreated)
        {
            await heartbeat.Value.ConfigureAwait(false);
        }
        await transport.StopAsync(cancellationToken).ConfigureAwait(false);
        await requestStream.StopAsync(cancellationToken).ConfigureAwait(false);
        await executor.DrainAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>所有する通信資源を解放します。</summary>
    /// <returns>解放完了。</returns>
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await ((IAsyncDisposable)transport).DisposeAsync().ConfigureAwait(false);
        await ((IAsyncDisposable)requestStream).DisposeAsync().ConfigureAwait(false);
        await ((IAsyncDisposable)repositorySync).DisposeAsync().ConfigureAwait(false);
        recovery.Dispose();
        replication.Dispose();
        participation.Dispose();
        stopping.Dispose();
    }
}
