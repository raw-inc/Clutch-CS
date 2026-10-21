using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Membership;

/// <summary>通信事実から参加状態とCoordinatorを選びます。</summary>
internal sealed class PeerMembership
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, PeerInfo> peers = new(StringComparer.Ordinal);
    private readonly PeerCoordinatorClaims claims = new();
    private readonly Dictionary<string, IReadOnlyList<string>> groups = new(StringComparer.Ordinal);
    private readonly ClusterOptions options;
    private readonly TimeProvider clock;
    private readonly Guid instance;
    private string? coordinator;
    private string? previousCoordinator;
    private long knownRevision;
    private long announcementSequence;
    private long coordinatorRevision;
    private string? revisionCoordinator;
    private Guid revisionInstance;

    /// <summary>観測した担当交代を保持し、以前の担当のメモリ値を復活させません。</summary>
    internal long CoordinatorRevision => Volatile.Read(ref coordinatorRevision);

    /// <summary>Core構成完了時に新しい実行値共有への対応を通知します。</summary>
    internal void EnableBufferedRuntime()
    {
        using (gate.EnterScope())
        {
            peers[options.PeerId] = peers[options.PeerId] with { BufferedRuntime = true };
        }
    }

    /// <summary>この参加実体の固定識別子です。</summary>
    internal Guid InstanceId => instance;

    /// <summary>内容分岐の確認で識別衝突を解除しません。</summary>
    internal bool IdentityConflict
    {
        get; private set;
    }

    /// <summary>時刻や一時的Ready状態を除く既知集合の変更番号です。</summary>
    public long KnownRevision => Volatile.Read(ref knownRevision);

    /// <summary>ローカル参加情報を作成します。</summary>
    public PeerMembership(ClusterOptions options, Guid instance, TimeProvider clock)
    {
        this.options = options;
        this.instance = instance;
        this.clock = clock;
        peers.Add(options.PeerId, new PeerInfo(options.PeerId, instance, options.HostId, options.BindAddress, options.Port, EPeerStatus.Joining, options.Priority, null, clock.GetUtcNow(), options.LogPort));
    }

    /// <summary>現在のCoordinatorを取得します。</summary>
    public string? Coordinator
    {
        get
        {
            using (gate.EnterScope())
            {
                return coordinator;
            }
        }
    }

    /// <summary>分断前の担当です。</summary>
    public string? PreviousCoordinator
    {
        get
        {
            using (gate.EnterScope())
            {
                return previousCoordinator;
            }
        }
    }

    /// <summary>不変の参加情報一覧を返します。</summary>
    /// <returns>現在観測できるPeer。</returns>
    public IReadOnlyList<PeerInfo> Snapshot()
    {
        using (gate.EnterScope())
        {
            return peers.Values.ToArray();
        }
    }

    /// <summary>状態と通知番号を同じローカルlock内で固定します。</summary>
    /// <returns>送信実体内で単調増加する通知です。</returns>
    internal PeerAnnouncement CreateAnnouncement(string digest)
    {
        using (gate.EnterScope())
        {
            return new(peers[options.PeerId], digest, coordinator, previousCoordinator, checked(++announcementSequence));
        }
    }

    /// <summary>保存待機中に後続通知や無通信へ進んだ古い宣言を使用しません。</summary>
    /// <returns>現在も受理済み状態と一致するかです。</returns>
    internal bool IsCurrent(PeerAnnouncement announcement)
    {
        using (gate.EnterScope())
        {
            return peers.TryGetValue(announcement.Peer.PeerId, out PeerInfo? peer) && peer.InstanceId == announcement.Peer.InstanceId
                && peer.Status == announcement.Peer.Status && claims.IsCurrent(announcement);
        }
    }

    /// <summary>過去の通常参加を現在の生存として復元しません。</summary>
    public void RestoreKnown(IReadOnlyList<PeerInfo> saved)
    {
        using (gate.EnterScope())
        {
            foreach (PeerInfo peer in saved)
            {
                if (!string.Equals(peer.PeerId, options.PeerId, StringComparison.Ordinal))
                {
                    peers.TryAdd(peer.PeerId, Retained(peer));
                }
            }
            knownRevision++;
        }
    }

    /// <summary>同一変更番号の既知集合を取得します。</summary>
    /// <returns>永続化する版とPeer一覧です。</returns>
    public (long Revision, PeerInfo[] Peers) ExportKnown()
    {
        using (gate.EnterScope())
        {
            return (knownRevision, peers.Values.Where(peer => !string.Equals(peer.PeerId, options.PeerId, StringComparison.Ordinal)).OrderBy(peer => peer.PeerId, StringComparer.Ordinal).Select(Retained).ToArray());
        }
    }

    /// <summary>共有設定のGroup集合を更新します。</summary>
    public void UpdateHostGroups(IReadOnlyDictionary<string, IReadOnlyList<string>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        using (gate.EnterScope())
        {
            groups.Clear();
            foreach (KeyValuePair<string, IReadOnlyList<string>> value in values)
            {
                groups[value.Key] = value.Value.Distinct(StringComparer.Ordinal).ToArray();
            }
        }
    }

    /// <summary>呼出元から変更できないGroup集合を返します。</summary>
    /// <returns>現在の所属。</returns>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> GetHostGroups()
    {
        using (gate.EnterScope())
        {
            return groups.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value.ToArray(), StringComparer.Ordinal);
        }
    }

    /// <summary>必要条件の確認結果をローカル状態へ反映します。</summary>
    /// <param name="status">反映する参加状態です。</param>
    /// <param name="preserveConflict">背景同期は、別方向のImportで検出済みの競合を上書きしません。</param>
    public void SetLocal(EPeerStatus status, bool preserveConflict = false)
    {
        using (gate.EnterScope())
        {
            PeerInfo current = peers[options.PeerId];
            if (preserveConflict && current.Status == EPeerStatus.Conflict)
            {
                return;
            }
            DateTimeOffset? ready = null;
            if (PeerStatusRules.CanParticipate(status))
            {
                ready = current.ReadySince ?? clock.GetUtcNow();
            }
            peers[options.PeerId] = current with { Status = status, ReadySince = ready, LastSeen = clock.GetUtcNow() };
            Elect();
        }
    }

    /// <summary>再bindしたローカルサービスの現在の接続先を反映します。</summary>
    public void SetLocalLogPort(int? logPort)
    {
        if (logPort is int port)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(port, ushort.MaxValue);
        }
        using (gate.EnterScope())
        {
            peers[options.PeerId] = peers[options.PeerId] with { LogPort = logPort };
        }
    }

    /// <summary>既知集合の永続変更を増やさず、現在の保存状態を通知します。</summary>
    public void SetLocalPersistenceStatus(PeerPersistenceStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentOutOfRangeException.ThrowIfNegative(status.UnsavedActive);
        ArgumentOutOfRangeException.ThrowIfNegative(status.UnsavedCandidates);
        ArgumentOutOfRangeException.ThrowIfNegative(status.LocalUnconfirmed);
        using (gate.EnterScope())
        {
            peers[options.PeerId] = peers[options.PeerId] with { PersistenceStatus = status };
        }
    }

    /// <summary>同じ既知実体の有効な要求・応答も生存として数え、参加状態そのものは昇格しません。</summary>
    internal void ObserveTraffic(string peerId, Guid instanceId)
    {
        using (gate.EnterScope())
        {
            if (peers.TryGetValue(peerId, out PeerInfo? peer) && peer.InstanceId == instanceId && instanceId != instance)
            {
                peers[peerId] = peer with { LastSeen = clock.GetUtcNow() };
            }
        }
    }

    /// <summary>送信元と一致する参加情報だけを記録します。</summary>
    /// <returns>正常な観測として受理したか。</returns>
    public bool Observe(PeerAnnouncement announcement, string address)
    {
        PeerInfo incoming = announcement.Peer;
        if (announcement.Sequence <= 0 || string.IsNullOrWhiteSpace(incoming.PeerId) || string.IsNullOrWhiteSpace(incoming.HostId) || incoming.InstanceId == Guid.Empty
            || !string.Equals(incoming.Address, address, StringComparison.Ordinal) || incoming.Port != options.Port)
        {
            return false;
        }
        using (gate.EnterScope())
        {
            if (peers.TryGetValue(incoming.PeerId, out PeerInfo? known) && known.InstanceId != incoming.InstanceId && known.Status is not (EPeerStatus.Unavailable or EPeerStatus.Detached))
            {
                // 既存通常参加者が特定できる場合は後発実体を採用しません。
                if (!PeerStatusRules.CanParticipate(known.Status) || (string.Equals(incoming.PeerId, options.PeerId, StringComparison.Ordinal) && PeerStatusRules.CanParticipate(incoming.Status) && incoming.ReadySince < known.ReadySince))
                {
                    peers[incoming.PeerId] = known with { Status = EPeerStatus.Conflict, ReadySince = null };
                    if (string.Equals(incoming.PeerId, options.PeerId, StringComparison.Ordinal))
                    {
                        IdentityConflict = true;
                    }
                }
                return false;
            }
            if (string.Equals(incoming.PeerId, options.PeerId, StringComparison.Ordinal))
            {
                return false;
            }
            if (!claims.Observe(announcement))
            {
                return false;
            }
            if (known is null || Retained(known) != Retained(incoming))
            {
                knownRevision++;
            }
            peers[incoming.PeerId] = incoming with { LastSeen = clock.GetUtcNow() };
            Elect();
            return true;
        }
    }

    /// <summary>無通信Peerを通常参加から除きます。</summary>
    public void Expire()
    {
        using (gate.EnterScope())
        {
            foreach (PeerInfo peer in peers.Values.ToArray())
            {
                if (peer.InstanceId != instance && peer.Status is not (EPeerStatus.Unavailable or EPeerStatus.Detached) && peer.LastSeen + options.PeerTimeout < clock.GetUtcNow())
                {
                    previousCoordinator ??= coordinator;
                    peers[peer.PeerId] = peer with { Status = EPeerStatus.Unavailable, ReadySince = null };
                }
            }
            Elect();
        }
    }

    /// <summary>現在担当を優先し、必要時だけ通常参加開始順で選びます。</summary>
    private void Elect()
    {
        ElectCurrent();
        Guid currentInstance = Guid.Empty;
        if (coordinator is not null && peers.TryGetValue(coordinator, out PeerInfo? current))
        {
            currentInstance = current.InstanceId;
        }
        if (!string.Equals(revisionCoordinator, coordinator, StringComparison.Ordinal) || revisionInstance != currentInstance)
        {
            revisionCoordinator = coordinator;
            revisionInstance = currentInstance;
            Interlocked.Increment(ref coordinatorRevision);
        }
    }

    /// <summary>現在の参加情報と担当宣言でCoordinatorを選びます。</summary>
    private void ElectCurrent()
    {
        claims.ForgetUnavailable(peers);
        PeerInfo[] ready = peers.Values.Where(peer => PeerStatusRules.CanParticipate(peer.Status)).OrderByDescending(peer => peer.Priority).ThenBy(peer => peer.ReadySince).ThenBy(peer => peer.PeerId, StringComparer.Ordinal).ToArray();
        if (ready.Length == 0)
        {
            coordinator = null;
            return;
        }
        PeerInfo? current = null;
        if (coordinator is not null)
        {
            current = peers.GetValueOrDefault(coordinator);
        }
        if (current is null || !PeerStatusRules.CanParticipate(current.Status) || current.Priority < ready[0].Priority)
        {
            coordinator = ready[0].PeerId;
        }
        coordinator = claims.Resolve(peers, peers[options.PeerId], coordinator, previousCoordinator);
        if (claims.IsAgreed(peers, instance, coordinator))
        {
            previousCoordinator = null;
        }
    }

    /// <summary>明示離脱だけを保ち、接続状態と時刻は再観測を必要とします。</summary>
    /// <returns>ローカル保持用の情報です。</returns>
    private static PeerInfo Retained(PeerInfo peer)
    {
        EPeerStatus status = EPeerStatus.Unavailable;
        if (peer.Status == EPeerStatus.Detached)
        {
            status = EPeerStatus.Detached;
        }
        return peer with { Status = status, ReadySince = null, LastSeen = DateTimeOffset.MinValue, LogPort = null, PersistenceStatus = null };
    }
}
