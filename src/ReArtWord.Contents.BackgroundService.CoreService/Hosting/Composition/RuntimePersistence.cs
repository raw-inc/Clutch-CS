using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.Availability;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Replication;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Loading;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Availability;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.State;

namespace ReArtWord.Contents.BackgroundService.CoreService.Hosting.Composition;

/// <summary>強い共有状態と用途別の候補保持を同じPeerの寿命へ接続します。</summary>
internal sealed class RuntimePersistence : IDisposable
{
    private readonly StateReplication replication;

    /// <summary>ノード状態の内部同期を所有し、通常get/setにだけ可用性方針を適用します。</summary>
    public RuntimePersistence(CommonConfiguration configuration, IDurableRepository repository, PeerCluster cluster)
    {
        Availability = new(cluster.LocalPeerId, configuration.Availability, repository, configuration.DataDirectory);
        State = new(repository, cluster.LocalPeerId);
        replication = new StateReplication(cluster, repository, State);
        AvailableState = new(State, Availability, () => cluster.GetPeers().Any(peer => peer.InstanceId == cluster.LocalInstanceId && (PeerStatusRules.CanParticipate(peer.Status) || peer.Status == EPeerStatus.Detached)));
        Availability.StateChanged = _ => PublishStatus(cluster);
        PublishStatus(cluster);
    }

    /// <summary>永続保存を要するCASと既存内部同期の状態です。</summary>
    public NodeStateStore State
    {
        get;
    }

    /// <summary>通常get/setと再接続時の内部候補の比較保存です。</summary>
    public AvailableNodeStateStore AvailableState
    {
        get;
    }

    /// <summary>設定と内部状態で共用する保存状態・有限候補です。</summary>
    public AvailabilityService Availability
    {
        get;
    }

    /// <summary>別のPeerへ接続するクライアントにも問題の所在を知らせます。</summary>
    private void PublishStatus(PeerCluster cluster)
    {
        IReadOnlyList<AvailabilityState> states = Availability.GetStates();
        cluster.SetLocalPersistenceStatus(new(states.Count(state => state.IsUnsaved), states.Count(state => state.CandidateIsUnsaved), states.Count(state => state.IsLocalUnconfirmed)));
    }

    /// <summary>所有する候補保持の同期資源を解放します。</summary>
    public void Dispose()
    {
        replication.Dispose();
        Availability.Dispose();
    }
}
