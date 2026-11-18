using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

/// <summary>参加実体の保持対応と基準日時を確認し、通常の保存を止めずに履歴整理だけを保留します。</summary>
public sealed class HistoryRetentionPeerGate
{
    private static string Topic => "history.retention-clock";
    private readonly PeerCluster cluster;
    private readonly TimeProvider time;
    private readonly TimeSpan lifetime;
    private readonly Lock gate = new();
    private readonly Dictionary<(string Peer, Guid Instance), Observation> observations = [];
    private readonly HashSet<(string Peer, Guid Instance)> confirmed = [];
    private long sequence;
    private string? publishProblem;

    /// <summary>生存通知と同じ期間だけ時刻通知を有効とし、送信元実体ごとに受付順序を確認します。</summary>
    public HistoryRetentionPeerGate(PeerCluster cluster, TimeProvider time, TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);
        this.cluster = cluster;
        this.time = time;
        this.lifetime = lifetime;
        Clock = new(time, cluster.LocalInstanceId);
        cluster.RegisterTransientHandler(Topic, ReceiveAsync);
    }

    /// <summary>一度引き継いだ基準は、元のPeerの離脱後も経過時間で進め続けます。</summary>
    public HistoryRetentionClock Clock
    {
        get;
    }

    /// <summary>整理だけを保留している理由です。</summary>
    public string? Problem
    {
        get; private set;
    }

    /// <summary>既存の一時通知で一度共有し、全Peerへの個別照会を繰り返しません。</summary>
    /// <returns>通知送信の一回の試行です。旧Coreは未登録の通知種別を処理しません。</returns>
    public async Task PublishAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await cluster.PublishTransientAsync(Topic, ContractJson.ToValue(new Announcement(Interlocked.Increment(ref sequence), Clock.Sample())), cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref publishProblem, null);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or OperationCanceledException))
        {
            // 時刻の補助通知だけの失敗で、通常の生存通知や開始済み処理の回復を止めません。
            // 履歴整理は次の通知成功まで保留し、削除を進めたことにしません。
            Volatile.Write(ref publishProblem, "History.RetentionClockUnconfirmed");
        }
    }

    /// <summary>現在の参加実体だけを確認し、未対応や古い通知を最新の時刻情報として使いません。</summary>
    /// <returns>新しい履歴境界を進められるかです。</returns>
    public async Task<bool> ReadyAsync(CancellationToken cancellationToken = default)
    {
        PeerInfo[] peers = cluster.GetPeers().Where(peer => peer.InstanceId != cluster.LocalInstanceId && PeerStatusRules.CanParticipate(peer.Status)).ToArray();
        confirmed.RemoveWhere(key => !peers.Any(peer => string.Equals(peer.PeerId, key.Peer, StringComparison.Ordinal) && peer.InstanceId == key.Instance));
        Problem = Volatile.Read(ref publishProblem);
        if (Problem is not null)
        {
            return false;
        }
        foreach (PeerInfo peer in peers)
        {
            (string, Guid) key = (peer.PeerId, peer.InstanceId);
            if (!confirmed.Contains(key))
            {
                if (!await SupportsAsync(peer, cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }
                confirmed.Add(key);
            }
            using (gate.EnterScope())
            {
                if (!observations.TryGetValue(key, out Observation? observation) || lifetime <= observation.Age.Elapsed)
                {
                    Problem = "History.RetentionClockUnconfirmed";
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>重複通知で鮮度を戻さず、現在観測している同じ実体の基準だけを引き継ぎます。</summary>
    /// <returns>一件の通知の反映です。</returns>
    internal Task ReceiveAsync(string peer, Guid instance, NodeValue value, CancellationToken cancellationToken = default)
    {
        if (instance == cluster.LocalInstanceId)
        {
            return Task.CompletedTask;
        }
        Announcement notice = value.Json.Deserialize<Announcement>(ContractJson.Options) ?? throw new InvalidDataException("履歴整理の基準がありません。");
        if (notice.Sequence < 1)
        {
            throw new InvalidDataException("履歴整理の通知順序が不正です。");
        }
        notice.Reference.Validate();
        HashSet<(string Peer, Guid Instance)> current = cluster.GetPeers().Select(item => (item.PeerId, item.InstanceId)).ToHashSet();
        using (gate.EnterScope())
        {
            foreach ((string Peer, Guid Instance) key in observations.Keys.Where(key => !current.Contains(key)).ToArray())
            {
                observations.Remove(key);
            }
            (string, Guid) source = (peer, instance);
            if (!current.Contains(source) || observations.TryGetValue(source, out Observation? previous) && notice.Sequence <= previous.Sequence)
            {
                return Task.CompletedTask;
            }
            Clock.Observe(notice.Reference, fromOrigin: notice.Reference.OriginId == instance);
            observations[source] = new(notice.Sequence, new(time));
        }
        return Task.CompletedTask;
    }

    /// <summary>保持境界の形式を理解する能力だけは、同じ実体の間で再利用します。</summary>
    /// <returns>対応を確認できたかです。</returns>
    private async Task<bool> SupportsAsync(PeerInfo peer, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            WireMessage? response = await cluster.ExchangeAsync("sync.capabilities", peer.PeerId, peer.InstanceId, JsonSerializer.SerializeToElement(Guid.Empty), deadline.Token).ConfigureAwait(false);
            SyncResponse? capability = response?.Body.Deserialize<SyncResponse>();
            if (capability is null)
            {
                Problem = "History.RetentionClockUnconfirmed";
                return false;
            }
            if (!capability.HistoryRetention)
            {
                Problem = "History.RetentionPeerUpgradeRequired";
                return false;
            }
            return true;
        }
        catch (Exception exception) when (SyncFailureException.IsExpected(exception))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Problem = "History.RetentionClockUnconfirmed";
            return false;
        }
    }

    /// <summary>連番は送信元Coreの通知順です。引き継いだ基準の発行元識別子は変えません。</summary>
    private sealed record Announcement(long Sequence, HistoryTimeReference Reference);

    /// <summary>期限後にも連番を保持し、同じ実体からの古い再送で鮮度を復活させません。</summary>
    private sealed record Observation(long Sequence, ObservationAge Age);
}
