/** 比較に使う値を照合して順序を決めます。
 * @returns 呼出し元が使用する値です。
 */
const compare = (a, b) => a < b ? -1 : (b) < (a) ? 1 : 0;

/** 対象が現在の操作に利用可能かを判定します。
 * @returns 呼出し元が使用する判定結果です。
 */
const available = peer => peer.status === 'ready' || peer.status === 'degraded';

/** 選択対象に含まれるホストの識別子を取得します。
 * @returns 呼出し元が使用する値です。
 */
export function hostIds(peers, clusterId = '')
{
    return [...new Set(peers.filter(peer => !clusterId || peer.clusterId === clusterId).map(peer => peer.hostId))].sort(compare);
}

/** 接続先が同じピアの同じ実体かを照合します。
 * @returns 呼出し元が使用する判定結果です。
 */
export function samePeer(first, second)
{
    return first?.clusterId === second?.clusterId && first?.peerId === second?.peerId && first?.instanceId === second?.instanceId;
}

/** 現在の選択に対応するピアを解決します。
 * @returns 呼出し元が使用する値です。
 */
export function currentPeer(peers, target)
{
    return !!target && peers.some(peer => samePeer(peer, target) && available(peer))
        && !peers.some(peer => peer.clusterId === target.clusterId && peer.peerId === target.peerId && peer.instanceId !== target.instanceId);
}

// A single cluster owns both endpoints. Resolve before browsing; never change it while enqueueing a copy.
/** 保存されたホスト指定を現在の候補へ対応付けます。
 * @returns clusterId・peers・problemを含む結果です。
 */
export function resolveHosts(peers, hosts, clusterId = '')
{
    const selected = hosts.filter(Boolean);
    if (!selected.length)
    {
        return { clusterId, peers: hosts.map(() => null) };
    }
    const eligible = peers.filter(peer => currentPeer(peers, peer));
    const clusters = clusterId ? [clusterId] : [...new Set(eligible.map(peer => peer.clusterId))].sort(compare);
    const common = clusters.find(cluster => selected.every(host => eligible.some(peer => peer.clusterId === cluster && peer.hostId === host)));
    if (!common)
    {
        return { clusterId: null, peers: hosts.map(() => null), problem: true };
    }
    return {
        clusterId: common,
        peers: hosts.map(host => eligible.filter(peer => peer.clusterId === common && peer.hostId === host)
            .sort((a, b) => (b.priority || 0) - (a.priority || 0) || compare(a.readySince || '', b.readySince || '') || compare(a.peerId, b.peerId))[0] || null)
    };
}
