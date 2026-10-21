using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Cluster;

namespace ReArtWord.Contents.BackgroundService.Runtime.Updates;

/// <summary>予約が既存の分散Updaterへ渡す検証・一回の開始・元結果照会です。</summary>
public interface IUpdateScheduleLauncher
{
    /// <summary>配布物と対象を確認します。更新自体は開始しません。</summary>
    /// <returns>確認したPeer実体です。</returns>
    public Task<IReadOnlyDictionary<string, Guid>> ValidateAsync(ClusterUpdateRequest request, CancellationToken cancellationToken = default);

    /// <summary>予約時の実体と一致した固定更新だけを起動します。</summary>
    /// <returns>開始確認または元IDで追跡する結果です。</returns>
    public Task<ClusterUpdateStatus> StartScheduledAsync(ClusterUpdateRequest request, IReadOnlyDictionary<string, Guid> instances, CancellationToken cancellationToken = default);

    /// <summary>再起動・不明からの復帰でも元IDを照会し、起動し直しません。</summary>
    /// <returns>元更新の確認結果です。</returns>
    public Task<ClusterUpdateStatus?> ReadAsync(Guid operationId, CancellationToken cancellationToken = default);
}
