using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Fleet;
using ReArtWord.Contents.BackgroundService.Runtime.Updates;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Fleet;

/// <summary>host順序の制御から、現行health照会と既存Updater配送を分離します。</summary>
public interface IFleetUpdateRemote
{
    /// <summary>固定host上の現在実体を、キャッシュを使わず確認します。</summary>
    /// <returns>当該hostの稼働確認です。</returns>
    public Task<FleetPeerObservation?> ObserveAsync(FleetUpdateTarget target, CancellationToken cancellationToken = default);

    /// <summary>発見期限から外れた停止中メンバーも、対象Coreの所属情報で確認します。</summary>
    /// <returns>所属ピアID、または確認不能です。</returns>
    public Task<IReadOnlyList<string>?> MembersAsync(FleetUpdateTarget target, CancellationToken cancellationToken = default);

    /// <summary>一試行の固定実体へ既存Updater配送を作ります。呼出側が解放します。</summary>
    /// <returns>一試行で所有する配送処理です。</returns>
    public IUpdatePeerTransport Create(ClusterUpdateRequest request, FleetUpdateTarget target);
}

/// <summary>実照会した実体、通常処理可能性と版です。</summary>
/// <param name="Peer">今回確認した実体です。</param>
/// <param name="Ready">Readyでmaintenanceがない場合だけtrueです。</param>
/// <param name="BuildVersion">確認した版です。</param>
public sealed record FleetPeerObservation(PeerInfo Peer, bool Ready, string? BuildVersion);
