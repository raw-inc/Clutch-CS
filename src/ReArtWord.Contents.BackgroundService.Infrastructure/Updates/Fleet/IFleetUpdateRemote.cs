using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Fleet;
using ReArtWord.Contents.BackgroundService.Runtime.Updates;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Fleet;

/// <summary>Fleet Updateで使用する稼働状態の照会と、Updaterへの通信を提供します。</summary>
public interface IFleetUpdateRemote
{
    /// <summary>更新対象のCoreServiceへ問い合わせ、現在の稼働状態を確認します。</summary>
    /// <returns>取得した稼働状態。確認できない場合はnull。</returns>
    public Task<FleetPeerObservation?> ObserveAsync(FleetUpdateTarget target, CancellationToken cancellationToken = default);

    /// <summary>対象CoreServiceが保持するクラスターの所属情報を取得します。</summary>
    /// <returns>所属ピアの一覧。確認できない場合はnull。</returns>
    public Task<IReadOnlyList<PeerInfo>?> MembersAsync(FleetUpdateTarget target, CancellationToken cancellationToken = default);

    /// <summary>更新対象との通信を作成します。使用後は呼び出し元で解放してください。</summary>
    /// <returns>この更新に使用する通信オブジェクト。</returns>
    public IUpdatePeerTransport Create(ClusterUpdateRequest request, FleetUpdateTarget target);
}

/// <summary>CoreServiceから取得した稼働状態とバージョン。</summary>
/// <param name="Peer">応答したCoreServiceのピア情報。</param>
/// <param name="Ready">Readyで、保守による処理保留が解除されている場合はtrue。</param>
/// <param name="BuildVersion">CoreServiceのバージョン。</param>
public sealed record FleetPeerObservation(PeerInfo Peer, bool Ready, string? BuildVersion);
