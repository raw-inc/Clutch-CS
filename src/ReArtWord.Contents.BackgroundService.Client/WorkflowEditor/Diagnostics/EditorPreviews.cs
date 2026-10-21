using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;

/// <summary>開いているワークフローの明示プレビューだけを照会し、媒体本体は利用者操作で取得します。</summary>
internal sealed class EditorPreviews(ManagementApi api)
{
    private DateTimeOffset nextRead;
    private string? currentCluster, currentWorkflow;
    private readonly List<NodePreviewSnapshot> snapshots = [];
    private readonly Dictionary<string, Guid> instances = new(StringComparer.Ordinal);
    private int generation;

    /// <summary>プレビュー結果の変更時だけキャンバスの表示を更新します。</summary>
    internal int Version
    {
        get; private set;
    }

    /// <summary>最新照会に応答しなかったPeerがあることを表示します。</summary>
    internal bool Unavailable
    {
        get; private set;
    }

    /// <summary>切断先の終了を未確認でも、Progressを無期限に実行中として表示しません。</summary>
    /// <returns>既存Preview、または有限保持中の進捗です。</returns>
    internal bool IsCurrent(NodePreviewSnapshot snapshot) => snapshot.Session is null || api.UtcNow - snapshot.CapturedAt < TimeSpan.FromMinutes(5);

    /// <summary>別クラスタ・別ワークフローの値を流用しません。</summary>
    /// <returns>指定ノードで実際に取得されたPeer別の結果です。</returns>
    internal IReadOnlyList<NodePreviewSnapshot> Find(string? cluster, string workflow, string node)
    {
        if (!string.Equals(cluster, currentCluster, StringComparison.Ordinal) || !string.Equals(workflow, currentWorkflow, StringComparison.Ordinal))
        {
            return [];
        }
        return snapshots.Where(item => string.Equals(item.NodeId, node, StringComparison.Ordinal) && IsCurrent(item)).OrderByDescending(item => item.CapturedAt).ToArray();
    }

    /// <summary>媒体データを定期配信せず、最大64ノードのメタデータだけを3秒間隔で照会します。</summary>
    /// <returns>成功したPeerの最新観測です。</returns>
    internal async Task RefreshAsync(string cluster, WorkflowDefinition document, IReadOnlyList<PeerInfo> peers, CancellationToken cancellationToken = default)
    {
        bool changed = !string.Equals(cluster, currentCluster, StringComparison.Ordinal) || !string.Equals(document.Id, currentWorkflow, StringComparison.Ordinal);
        if (!changed && api.UtcNow < nextRead)
        {
            return;
        }
        int version = ++generation;
        if (changed)
        {
            snapshots.Clear();
            instances.Clear();
            Unavailable = false;
            Version++;
        }
        currentCluster = cluster;
        currentWorkflow = document.Id;
        nextRead = api.UtcNow.AddSeconds(3);
        bool recipe = SubworkflowInterface.IsSubworkflow(document);
        string[] nodes = document.Nodes.Where(node => !recipe && node.Type is "control.preview" or "control.progress").Select(node => node.Id).Take(64).ToArray();
        if (nodes.Length == 0)
        {
            if (changed || snapshots.Count != 0 || Unavailable)
            {
                Version++;
            }
            snapshots.Clear();
            Unavailable = false;
            return;
        }
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        List<NodePreviewSnapshot> found = [];
        HashSet<string> confirmed = new(StringComparer.Ordinal);
        PeerInfo[] selected = peers.DistinctBy(peer => peer.PeerId).Take(256).ToArray();
        bool unavailable = false;
        foreach (PeerInfo[] batch in selected.Chunk(4))
        {
            IReadOnlyList<NodePreviewSnapshot>?[] results = await Task.WhenAll(batch.Select(ReadPeerAsync)).ConfigureAwait(true);
            for (int index = 0; index < batch.Length; index++)
            {
                if (results[index] is not null)
                {
                    confirmed.Add(batch[index].PeerId);
                }
            }
            unavailable |= results.Any(result => result is null);
            found.AddRange(results.OfType<IReadOnlyList<NodePreviewSnapshot>>().SelectMany(result => result));
            if (timeout.IsCancellationRequested)
            {
                unavailable = true;
                break;
            }
        }
        if (version == generation && string.Equals(api.SelectedClusterId, cluster, StringComparison.Ordinal))
        {
            HashSet<string> retained = selected.Where(peer => !confirmed.Contains(peer.PeerId) && instances.GetValueOrDefault(peer.PeerId) == peer.InstanceId)
                .Select(peer => peer.PeerId).ToHashSet(StringComparer.Ordinal);
            found.AddRange(snapshots.Where(item => retained.Contains(item.PeerId) && nodes.Contains(item.NodeId, StringComparer.Ordinal)));
            if (changed || Unavailable != unavailable || !snapshots.SequenceEqual(found))
            {
                Version++;
            }
            snapshots.Clear();
            snapshots.AddRange(found);
            instances.Clear();
            foreach (PeerInfo peer in selected.Where(peer => confirmed.Contains(peer.PeerId) || retained.Contains(peer.PeerId)))
            {
                instances[peer.PeerId] = peer.InstanceId;
            }
            Unavailable = unavailable;
        }

        async Task<IReadOnlyList<NodePreviewSnapshot>?> ReadPeerAsync(PeerInfo peer)
        {
            try
            {
                IReadOnlyList<NodePreviewSnapshot> values = await api.PostForPeerAsync<List<NodePreviewSnapshot>>("/api/previews/list", new NodePreviewQuery(document.Id, nodes), peer.PeerId, cluster, cancellationToken: timeout.Token).ConfigureAwait(true) ?? [];
                return values.Where(item => string.Equals(item.WorkflowId, document.Id, StringComparison.Ordinal)
                    && string.Equals(item.PeerId, peer.PeerId, StringComparison.Ordinal) && nodes.Contains(item.NodeId, StringComparer.Ordinal)).ToArray();
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException)
            {
                return null;
            }
        }
    }

    /// <summary>固定した取得IDを分割して読み、更新・失効で異なる本文を混ぜません。</summary>
    /// <returns>上限を確認した所有済み本文です。</returns>
    internal async Task<byte[]> ReadAsync(string cluster, NodePreviewSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(snapshot.Length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(snapshot.Length, 16 * 1024 * 1024);
        byte[] result = new byte[snapshot.Length];
        int offset = 0;
        while (offset < result.Length)
        {
            int length = Math.Min(32768, result.Length - offset);
            NodePreviewChunk chunk = await api.PostForPeerAsync<NodePreviewChunk>("/api/previews/chunk", new NodePreviewChunkRequest(snapshot.Id, offset, length), snapshot.PeerId, cluster, cancellationToken: cancellationToken).ConfigureAwait(true)
                ?? throw new IOException("preview.read");
            if (chunk.Id != snapshot.Id || chunk.Offset != offset || chunk.Data.Length != length)
            {
                throw new IOException("preview.changed");
            }
            chunk.Data.CopyTo(result.AsMemory(offset));
            offset += length;
        }
        return result;
    }

    /// <summary>履歴表示中だけ一つのPeerを照会し、別WF・ノード・Peerの結果を混ぜません。</summary>
    /// <returns>表示対象に属する最大50件の履歴です。</returns>
    internal async Task<IReadOnlyList<NodePreviewHistoryEntry>> ReadHistoryAsync(string cluster, NodePreviewSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<NodePreviewHistoryEntry> rows = await api.PostForPeerAsync<List<NodePreviewHistoryEntry>>("/api/previews/history",
            new NodePreviewHistoryQuery(snapshot.WorkflowId, snapshot.NodeId), snapshot.PeerId, cluster, cancellationToken: cancellationToken).ConfigureAwait(true) ?? [];
        return rows.Where(row => string.Equals(row.Snapshot.WorkflowId, snapshot.WorkflowId, StringComparison.Ordinal)
            && string.Equals(row.Snapshot.NodeId, snapshot.NodeId, StringComparison.Ordinal) && string.Equals(row.Snapshot.PeerId, snapshot.PeerId, StringComparison.Ordinal))
            .OrderByDescending(row => row.Number).Take(50).ToArray();
    }
}
