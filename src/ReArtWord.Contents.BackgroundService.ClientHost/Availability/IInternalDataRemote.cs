using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.InternalData;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Availability;

/// <summary>内部状態だけの実体確認・読取り・比較更新を提供します。</summary>
internal interface IInternalDataRemote
{
    /// <summary>発見した一つの実体を確認します。</summary>
    /// <returns>未確認時nullです。</returns>
    public PeerInfo? Find(string clusterId, string peerId);

    /// <summary>固定実体の内部状態だけを取得します。</summary>
    /// <returns>確認済み保存値と現在値です。</returns>
    public Task<InternalDataReadResult> ReadAsync(string clusterId, PeerInfo peer, InternalDataTarget target, CancellationToken cancellationToken = default);

    /// <summary>固定実体へ元IDの内部値更新を一回だけ送ります。</summary>
    /// <returns>操作IDと実保存結果です。</returns>
    public Task<InternalDataWriteResult> WriteAsync(string clusterId, PeerInfo peer, InternalDataWriteRequest request, bool compareExchange, CancellationToken cancellationToken = default);

    /// <summary>元要求を再実行せず、保存された固定結果だけを照会します。</summary>
    /// <returns>確定した内側結果、または未確認のnullです。</returns>
    public Task<InternalDataWriteResult?> QueryAsync(Guid commandId, Guid operationId, CancellationToken cancellationToken = default);
}
