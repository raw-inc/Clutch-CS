using System.Text.Json;
using ReArtWord.Contents.BackgroundService.ClientHost.Management;
using ReArtWord.Contents.BackgroundService.ClientHost.Networking;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.InternalData;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Availability;

/// <summary>同じUDP Observerと永続要求IDを内部状態専用経路でも共用します。</summary>
internal sealed class ObserverInternalDataRemote(ObserverConnection connection, ClientCommandLedger ledger) : IInternalDataRemote
{
    /// <summary>選択クラスタとPeerの一意な実体だけを返します。</summary>
    /// <returns>発見済み実体またはnullです。</returns>
    public PeerInfo? Find(string clusterId, string peerId)
    {
        PeerInfo[] peers = connection.GetClusters().Where(cluster => string.Equals(cluster.ClusterId, clusterId, StringComparison.Ordinal))
            .SelectMany(cluster => cluster.Peers).Where(peer => string.Equals(peer.PeerId, peerId, StringComparison.Ordinal)).ToArray();
        if (peers.Length == 1)
        {
            return peers[0];
        }
        return null;
    }

    /// <summary>内部値の確認だけを副作用なしで実行します。</summary>
    /// <returns>型付き内部状態です。</returns>
    public async Task<InternalDataReadResult> ReadAsync(string clusterId, PeerInfo peer, InternalDataTarget target, CancellationToken cancellationToken = default)
    {
        if (connection.FindObserver(clusterId, peer.PeerId) is not { } observer)
        {
            throw new IOException("InternalData.Offline");
        }
        ManagementCommand command = new(Guid.NewGuid(), clusterId, peer.PeerId, peer.InstanceId, DateTimeOffset.UtcNow.AddSeconds(5), "internal-data.read", NodeValue.FromObject(target));
        ManagementReply reply = await observer.InspectAsync(command, cancellationToken).ConfigureAwait(false);
        if (reply.Outcome == EOutcome.Unknown)
        {
            throw new IOException("InternalData.Offline");
        }
        if (reply.Outcome != EOutcome.Succeeded)
        {
            throw new InvalidOperationException(reply.Reason ?? "InternalData.ReadRejected");
        }
        return reply.Value?.Json.Deserialize<InternalDataReadResult>(ContractJson.Options) ?? throw new InvalidDataException("内部状態の応答がありません。");
    }

    /// <summary>対象・期限・内容を同じledgerで固定し、切断後も暗黙再送しません。</summary>
    /// <returns>内側操作IDまで確認した保存結果です。</returns>
    public async Task<InternalDataWriteResult> WriteAsync(string clusterId, PeerInfo peer, InternalDataWriteRequest request, bool compareExchange, CancellationToken cancellationToken = default)
    {
        string operation = "internal-data.write";
        if (compareExchange)
        {
            operation = "internal-data.compare-exchange";
        }
        if (connection.FindObserver(clusterId, peer.PeerId) is not { } observer || Find(clusterId, peer.PeerId)?.InstanceId != peer.InstanceId)
        {
            return new(request.OperationId, new(EOutcome.NotExecuted, false, null, "InternalData.InstanceUnavailable"));
        }
        Guid id = ClientCommandLedger.Identity(request.OperationId, operation);
        ManagementCommand command = await ledger.PrepareAsync(id, clusterId, peer.PeerId, operation, NodeValue.FromObject(request), peer, cancellationToken).ConfigureAwait(false);
        ManagementReply reply = await observer.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
        if (reply.Outcome != EOutcome.Succeeded)
        {
            return new(request.OperationId, new(reply.Outcome, false, null, reply.Reason));
        }
        InternalDataWriteResult result = reply.Value?.Json.Deserialize<InternalDataWriteResult>(ContractJson.Options) ?? throw new InvalidDataException("内部更新の応答がありません。");
        if (result.OperationId != request.OperationId)
        {
            throw new InvalidDataException("内部更新の元IDが一致しません。");
        }
        return result;
    }

    /// <summary>ledgerで固定した元の実体・指紋の操作記録だけを読みます。</summary>
    /// <returns>同じ内側操作の確定結果またはnullです。</returns>
    public async Task<InternalDataWriteResult?> QueryAsync(Guid commandId, Guid operationId, CancellationToken cancellationToken = default)
    {
        ClientCommandRecord? record = await ledger.ReadAsync(commandId, cancellationToken).ConfigureAwait(false);
        if (record is null || connection.FindObserver(record.ClusterId, record.TargetPeerId) is not { } observer)
        {
            return null;
        }
        ManagementQueryResult query = await observer.QueryAsync(new ManagementReceiptQuery(record.RequestId, record.ClusterId, record.TargetPeerId, record.RequestFingerprint), cancellationToken).ConfigureAwait(false);
        if (query.Running || query.Reply is null || query.Reply.Outcome == EOutcome.Unknown)
        {
            return null;
        }
        if (query.Reply.Outcome != EOutcome.Succeeded)
        {
            return new(operationId, new(query.Reply.Outcome, false, null, query.Reply.Reason));
        }
        InternalDataWriteResult? result = query.Reply.Value?.Json.Deserialize<InternalDataWriteResult>(ContractJson.Options);
        if (result?.OperationId != operationId)
        {
            throw new InvalidDataException("照会結果の内側操作IDが一致しません。");
        }
        return result;
    }
}
