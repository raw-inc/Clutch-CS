using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Health;

/// <summary>Updaterの一回の寿命で既存UDP 監視処理を所有します。</summary>
public sealed class UpdateObserver : IAsyncDisposable
{
    private readonly UdpObserver observer;
    private readonly string clusterId;

    /// <summary>明示NICと固定クラスタを使用しCore Peerとして参加しません。</summary>
    public UpdateObserver(ClientNetworkBinding binding, string clusterId, TimeSpan? requestTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentException.ThrowIfNullOrWhiteSpace(clusterId);
        this.clusterId = clusterId;
        observer = new(new ObserverOptions("updater-" + Guid.NewGuid().ToString("N"), binding) { RequestTimeout = requestTimeout ?? TimeSpan.FromSeconds(2) });
    }

    /// <summary>共通更新設定から明示通信範囲を取り出します。</summary>
    /// <returns>既存監視処理に渡す通信設定です。</returns>
    public static ClientNetworkBinding Binding(UpdatesOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new(options.InterfaceId ?? throw new InvalidDataException("更新NICがありません。"), options.LocalAddress ?? throw new InvalidDataException("更新localAddressがありません。"), options.BroadcastAddress ?? throw new InvalidDataException("更新broadcastがありません。"), options.ManagementPort);
    }

    /// <summary>旧版のTCP受付に渡すため、送信に使うIP上の既存ピアだけを確認します。</summary>
    /// <returns>同じクラスターで観測したピア名、該当しない場合はnullです。</returns>
    public string? FindLocalPeerId()
    {
        string address = observer.CaptureBindingStatus().Binding.BindAddress;
        return observer.GetClusters().Where(cluster => string.Equals(cluster.ClusterId, clusterId, StringComparison.Ordinal))
            .SelectMany(cluster => cluster.Peers).FirstOrDefault(peer => string.Equals(peer.Address, address, StringComparison.Ordinal))?.PeerId;
    }

    /// <summary>指定Peerを実announcementで確認し、書込み対象の実体を差し替えません。</summary>
    /// <returns>確認した実体です。</returns>
    public async Task<PeerInfo> FindAsync(string peerId, Guid? expectedInstance = null, CancellationToken cancellationToken = default)
    {
        await observer.StartAsync(cancellationToken).ConfigureAwait(false);
        using CancellationTokenSource limit = new(TimeSpan.FromSeconds(10));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(limit.Token, cancellationToken);
        while (!linked.IsCancellationRequested)
        {
            PeerInfo? peer = observer.GetClusters().Where(item => string.Equals(item.ClusterId, clusterId, StringComparison.Ordinal)).SelectMany(item => item.Peers).Where(item => string.Equals(item.PeerId, peerId, StringComparison.Ordinal) && (!expectedInstance.HasValue || item.InstanceId == expectedInstance.Value)).OrderByDescending(item => item.LastSeen).FirstOrDefault();
            if (peer is not null)
            {
                return peer;
            }
            await Task.Delay(50, linked.Token).ConfigureAwait(false);
        }
        throw new OperationCanceledException(linked.Token);
    }

    /// <summary>一度だけ実行し、応答消失時は元操作記録の照会だけを行います。</summary>
    /// <returns>同じ元要求で確認できた結果です。</returns>
    public async Task<ManagementReply> SendAsync(string peerId, Guid instanceId, string operation, NodeValue? payload, bool readOnly, CancellationToken cancellationToken = default)
    {
        await observer.StartAsync(cancellationToken).ConfigureAwait(false);
        ManagementCommand command = new(Guid.NewGuid(), clusterId, peerId, instanceId, DateTimeOffset.UtcNow.AddMinutes(2), operation, payload);
        return await SendAsync(command, readOnly, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>外側識別子も固定済みの停止要求などを送ります。</summary>
    /// <returns>実応答または未確認です。</returns>
    public async Task<ManagementReply> SendAsync(ManagementCommand command, bool readOnly, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        await observer.StartAsync(cancellationToken).ConfigureAwait(false);
        if (readOnly)
        {
            return await observer.InspectWithRetryAsync(command, cancellationToken).ConfigureAwait(false);
        }
        ManagementReply reply = await observer.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
        string unconfirmedQuery = "Observer.ReceiptUnconfirmed";
        string notYetRecorded = "Execution.ReceiptMissing";
        while (reply.Outcome == EOutcome.Unknown && DateTimeOffset.UtcNow < command.ExpiresAt)
        {
            ManagementQueryResult queried = await observer.QueryAsync(command, cancellationToken).ConfigureAwait(false);
            if (queried.Reply is not null)
            {
                return queried.Reply;
            }

            // 照会パケット自体の欠測は「元処理が止まった」根拠にせず、期限内は読取りだけを続けます。
            if (!queried.Running && !string.Equals(queried.Reason, unconfirmedQuery, StringComparison.Ordinal)
                && !string.Equals(queried.Reason, notYetRecorded, StringComparison.Ordinal))
            {
                return reply;
            }
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        return reply;
    }

    /// <summary>ドメイン DTOのJSON所有権とenum表現を共通化します。</summary>
    /// <returns>既存管理protocolのペイロードです。</returns>
    public static NodeValue Payload<T>(T value) => NodeValue.FromJson(JsonSerializer.SerializeToElement(value, ContractJson.Options));

    /// <summary>自分の監視ソケットだけを解放します。</summary>
    /// <returns>監視終了です。</returns>
    ValueTask IAsyncDisposable.DisposeAsync() => ((IAsyncDisposable)observer).DisposeAsync();
}
