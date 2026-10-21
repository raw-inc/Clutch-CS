using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Targets;

namespace ReArtWord.Contents.BackgroundService.Runtime.Targets;

/// <summary>固定した参加情報と観測から、重複しない実行PCを選びます。</summary>
public sealed class PcSelectionResolver
{
    private readonly IReadOnlyList<PeerInfo> peers;
    private readonly IReadOnlyDictionary<string, ResourceSample> observations;
    private readonly DateTimeOffset now;
    private readonly bool detached;
    private readonly Guid localInstance;

    /// <summary>この一件で使用する参加集合と観測を固定します。</summary>
    public PcSelectionResolver(IReadOnlyList<PeerInfo> peers, IReadOnlyDictionary<string, ResourceSample> observations, DateTimeOffset now, Guid localInstance)
    {
        ArgumentNullException.ThrowIfNull(peers);
        ArgumentNullException.ThrowIfNull(observations);
        this.peers = peers.ToArray();
        this.observations = observations.ToDictionary(pair => pair.Key, pair => pair.Value with { Metrics = pair.Value.Metrics.ToArray() }, StringComparer.Ordinal);
        this.now = now;
        this.localInstance = localInstance;
        detached = peers.Any(peer => peer.InstanceId == localInstance && peer.Status == EPeerStatus.Detached);
    }

    /// <summary>候補を合わせてPC単位で重複を除き、指定された台数を選びます。</summary>
    /// <returns>実行先と選択根拠。候補不成立の場合は未実行の理由。</returns>
    public TargetResolution Resolve(IReadOnlyList<PcSelection> selections, EPcExecutionMode mode)
    {
        ArgumentNullException.ThrowIfNull(selections);
        List<PcSelectionObservation> compared = [];
        try
        {
            _ = PcSelectionValue.Read(NodeValue.FromObject(selections));
            if (!Enum.IsDefined(mode))
            {
                throw new FormatException("PC実行台数の指定が不正です。");
            }
            HashSet<string> hosts = [];
            foreach (PcSelection selection in selections)
            {
                hosts.UnionWith(Select(selection, compared));
            }
            Dictionary<string, string> problems = new(StringComparer.Ordinal);
            foreach (string host in hosts)
            {
                PeerInfo? peer = HostPeer(host);
                if (peer is null)
                {
                    problems[host] = "selection.pcMissing";
                }
                else
                {
                    if (!Available(peer))
                    {
                        problems[host] = "selection.pcUnavailable";
                    }
                }
            }
            PcSelectionDecision decision = new(now, hosts.Order(StringComparer.Ordinal).ToArray(), compared.ToArray(), problems);
            PeerInfo[] targets = hosts.Select(HostPeer).Where(peer => peer is not null).Cast<PeerInfo>().ToArray();
            if (hosts.Count == 0 || targets.Length == 0 || mode == EPcExecutionMode.All && targets.Length != hosts.Count)
            {
                return Failure("selection.noCandidates", decision);
            }
            if (mode == EPcExecutionMode.One)
            {
                PeerInfo? selected = Order(targets.Where(Available)).FirstOrDefault();
                if (selected is null)
                {
                    if (detached)
                    {
                        return Failure("selection.noCandidates", decision);
                    }
                    selected = Order(targets).First();
                }
                targets = [selected];
            }
            EExecutionScope scope = mode == EPcExecutionMode.All ? EExecutionScope.All
                                                                 : EExecutionScope.One;
            return new(scope, targets, Selection: decision);
        }
        catch (Exception error) when (error is FormatException or System.Text.Json.JsonException or ArgumentException)
        {
            return Failure("selection.invalid", new(now, [], compared.ToArray()));
        }
    }

    /// <summary>一つの選択結果を求め、低負荷選択の欠測も根拠に残します。</summary>
    /// <returns>この選択が返したPC識別子。</returns>
    private IReadOnlyList<string> Select(PcSelection selection, List<PcSelectionObservation> compared)
    {
        if (selection.Lowest is not { } resource)
        {
            if (selection.AllHosts)
            {
                if (detached)
                {
                    return peers.Where(Available).Select(peer => peer.HostId).Distinct(StringComparer.Ordinal).ToArray();
                }
                return peers.Where(peer => peer.Status != EPeerStatus.Detached).Select(peer => peer.HostId).Distinct(StringComparer.Ordinal).ToArray();
            }
            return selection.HostIds!;
        }
        IEnumerable<string> hosts = peers.Where(Available).Select(peer => peer.HostId);
        if (selection.Within is not null)
        {
            hosts = selection.Within.SelectMany(item => Select(item, compared));
        }
        List<(PeerInfo Peer, double Value)> measured = [];
        foreach (string host in hosts.Distinct(StringComparer.Ordinal))
        {
            PeerInfo? peer = HostPeer(host);
            if (peer is null)
            {
                string unit = resource.UsedBytes ? "bytes"
                                                 : "percent";
                compared.Add(new(host, string.Empty, Guid.Empty, resource.Resource, null, unit, null, Reason: "selection.pcMissing"));
                continue;
            }
            PcSelectionObservation observation = PcResourceComparison.Read(peer, observations.GetValueOrDefault(peer.PeerId), resource, now);
            if (!Available(peer))
            {
                observation = observation with { Value = null, Reason = "selection.pcUnavailable" };
            }
            compared.Add(observation);
            if (observation.Value is double value)
            {
                measured.Add((peer, value));
            }
        }
        if (measured.Count == 0)
        {
            return [];
        }
        double lowest = measured.Min(item => item.Value);
        PeerInfo selected = Order(measured.Where(item => item.Value == lowest).Select(item => item.Peer)).First();
        return [selected.HostId];
    }

    /// <summary>同じPCの実行Peerを参加可能性と優先度で一つに決めます。</summary>
    /// <returns>所属を確認できるPeer。未発見のPCはnull。</returns>
    private PeerInfo? HostPeer(string host) => Order(peers.Where(peer => string.Equals(peer.HostId, host, StringComparison.Ordinal)))
        .OrderByDescending(Available).FirstOrDefault();

    /// <summary>離脱中は自分の実体だけを実行対象にします。</summary>
    /// <returns>この一件で実行候補になれるか。</returns>
    private bool Available(PeerInfo peer) => detached ? peer.InstanceId == localInstance && peer.Status == EPeerStatus.Detached
                                                      : PeerStatusRules.CanParticipate(peer.Status);

    /// <summary>同順位でも列挙順に左右されない順序を使います。</summary>
    /// <returns>優先度、連続参加時刻、識別子の順で並べた候補。</returns>
    private static IOrderedEnumerable<PeerInfo> Order(IEnumerable<PeerInfo> candidates) => candidates.OrderByDescending(peer => peer.Priority)
        .ThenBy(peer => peer.ReadySince).ThenBy(peer => peer.PeerId, StringComparer.Ordinal);

    /// <summary>候補不成立を未指定や別PCへの代替へ読み替えません。</summary>
    /// <returns>比較根拠を保持した未実行結果。</returns>
    private static TargetResolution Failure(string reason, PcSelectionDecision decision)
        => new(EExecutionScope.One, [], NodeResult.Failure(reason, "指定されたPC選択を満たす実行先がありません。選択結果と観測状態を確認してください。", EOutcome.NotExecuted), decision);
}
