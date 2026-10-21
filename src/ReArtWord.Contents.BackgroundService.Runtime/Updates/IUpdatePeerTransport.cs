using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Cluster;

namespace ReArtWord.Contents.BackgroundService.Runtime.Updates;

/// <summary>固定Peer集合の更新を共通クラスタ通信へ接続する境界です。</summary>
public interface IUpdatePeerTransport
{
    /// <summary>転送や保留の前に対象Peerの実RIDを確認します。</summary>
    /// <returns>現在の実行環境のRIDです。</returns>
    public Task<string> GetPlatformAsync(string peerId, CancellationToken cancellationToken = default);

    /// <summary>分割転送と全チェックサムを確認して更新準備を固定します。</summary>
    /// <returns>実行可能な準備結果です。</returns>
    public Task<UpdatePeerResult> StageAsync(ClusterUpdateRequest request, string peerId, CancellationToken cancellationToken = default);

    /// <summary>準備済みのstandaloneUpdaterを一度だけ起動します。</summary>
    /// <returns>受理状態であり完了成功ではありません。</returns>
    public Task<UpdatePeerResult> DispatchAsync(Guid operationId, string peerId, CancellationToken cancellationToken = default);

    /// <summary>再起動後も同じ操作の確認済み状態を読みます。</summary>
    /// <returns>実行結果、または未確認状態です。</returns>
    public Task<UpdatePeerResult> ReadAsync(Guid operationId, string peerId, CancellationToken cancellationToken = default);

    /// <summary>成功を確認した更新を明示して旧リリースへ戻します。</summary>
    /// <returns>復元処理の受理または結果です。</returns>
    public Task<UpdatePeerResult> RollbackAsync(Guid operationId, string peerId, CancellationToken cancellationToken = default);

    /// <summary>互換性または復元を確認できないPeerを通常処理から外します。</summary>
    /// <returns>除外の確認です。</returns>
    public Task ExcludeAsync(string peerId, CancellationToken cancellationToken = default);

    /// <summary>事前設定された方針に従いクラスタの通常処理を停止します。</summary>
    /// <returns>停止要求の確認です。</returns>
    public Task StopClusterAsync(Guid operationId, IReadOnlyList<string> peerIds, CancellationToken cancellationToken = default);

    /// <summary>完了と互換性を確認したPeerの更新maintenanceを解除します。</summary>
    /// <returns>同じ操作識別子に対する解除確認です。</returns>
    public Task ReleaseAsync(Guid operationId, string peerId, CancellationToken cancellationToken = default);
}
