using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow.Dashboards;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Dashboards;

/// <summary>表示中の参照だけを観測し、別クラスタや別タブへ遅延応答を持ち越しません。</summary>
internal sealed class WfDashboardSession(ManagementApi api)
{
    private readonly Dictionary<WfDashboardReference, WfDashboardNodeState> states = [];
    private readonly Dictionary<WfDashboardReference, bool?> toggles = [];
    private readonly Dictionary<(string Workflow, int Batch), EditorPreviews> previews = [];
    private static readonly JsonElement empty = JsonSerializer.SerializeToElement(new { });
    private int generation;

    /// <summary>現在の接続先へ取得が成功した場合だけ操作を有効にします。</summary>
    internal bool Available
    {
        get; private set;
    }

    /// <summary>カードの元ノード情報です。</summary>
    /// <returns>取得できた同じ物理参照の状態です。</returns>
    internal WfDashboardNodeState? Find(WfDashboardReference reference) => states.GetValueOrDefault(reference);

    /// <summary>確認済みの共有トグル状態です。</summary>
    /// <returns>未確認時はnullです。</returns>
    internal bool? Toggle(WfDashboardReference reference) => toggles.GetValueOrDefault(reference);

    /// <summary>元のWFとノードの観測だけをカードへ渡します。</summary>
    /// <returns>Peerごとの最新スナップショットです。</returns>
    internal IReadOnlyList<NodePreviewSnapshot> Preview(string cluster, WfDashboardReference reference)
        => previews.Where(item => string.Equals(item.Key.Workflow, reference.WorkflowId, StringComparison.Ordinal))
            .SelectMany(item => item.Value.Find(cluster, reference.WorkflowId, reference.NodeId)).ToArray();

    /// <summary>書込み後は古い確認結果を操作可能な現在値として扱いません。</summary>
    internal void Invalidate()
    {
        generation++;
        Available = false;
        toggles.Clear();
    }

    /// <summary>同じ参照への重複取得を除き、元WFの本文を転送せず状態を更新します。</summary>
    /// <returns>表示中カードだけの有限な観測です。</returns>
    internal async Task RefreshAsync(string cluster, string peer, IReadOnlyList<WfDashboardReference> references, IReadOnlyList<PeerInfo> peers, CancellationToken cancellationToken = default)
    {
        int requested = ++generation;
        WfDashboardReference[] selected = references.Distinct().Take(128).ToArray();
        foreach (WfDashboardReference obsolete in toggles.Keys.Where(reference => !selected.Contains(reference)).ToArray())
        {
            toggles.Remove(obsolete);
        }
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            List<WfDashboardNodeState> fetched = [];
            foreach (WfDashboardReference[] batch in selected.Chunk(64))
            {
                IReadOnlyList<WfDashboardNodeState> response = await api.PostForPeerAsync<List<WfDashboardNodeState>>("/api/dashboards/nodes", new WfDashboardNodeQuery(batch), peer, cluster, cancellationToken: timeout.Token).ConfigureAwait(true) ?? [];
                fetched.AddRange(response.Where(item => batch.Contains(item.Reference)));
            }
            if (!Current(requested, cluster, peer))
            {
                return;
            }
            states.Clear();
            foreach (WfDashboardNodeState state in fetched)
            {
                states[state.Reference] = state;
            }
            Available = selected.All(states.ContainsKey);
            foreach (WfDashboardReference[] batch in selected.Where(reference => reference.NodeType is "control.toggleButton").Chunk(4))
            {
                bool?[] values = await Task.WhenAll(batch.Select(reference => EditorToggleStates.ReadStateAsync(api, cluster, peer, reference.WorkflowId, reference.NodeId, timeout.Token))).ConfigureAwait(true);
                if (!Current(requested, cluster, peer))
                {
                    return;
                }
                for (int index = 0; index < batch.Length; index++)
                {
                    toggles[batch[index]] = values[index];
                }
            }
            HashSet<(string Workflow, int Batch)> retained = [];
            PeerInfo[] available = peers.Where(item => PeerStatusRules.CanParticipate(item.Status) || item.Status == EPeerStatus.Detached).Take(64).ToArray();
            foreach (IGrouping<string, WfDashboardReference> group in selected.Where(reference => reference.NodeType is "control.preview" or "control.progress").GroupBy(reference => reference.WorkflowId, StringComparer.Ordinal))
            {
                int index = 0;
                foreach (WfDashboardReference[] batch in group.Chunk(64))
                {
                    (string Workflow, int Batch) key = (group.Key, index++);
                    retained.Add(key);
                    if (!previews.TryGetValue(key, out EditorPreviews? reader))
                    {
                        reader = new(api);
                        previews.Add(key, reader);
                    }
                    WorkflowDefinition selection = new(group.Key, group.Key, batch.Select(reference => new NodeDefinition(reference.NodeId, reference.NodeType, empty)).ToArray(), []);
                    await reader.RefreshAsync(cluster, selection, available, timeout.Token).ConfigureAwait(true);
                    if (!Current(requested, cluster, peer))
                    {
                        return;
                    }
                }
            }
            foreach ((string Workflow, int Batch) key in previews.Keys.Where(key => !retained.Contains(key)).ToArray())
            {
                previews.Remove(key);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException)
        {
            if (requested == generation)
            {
                Available = false;
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <summary>更新途中の操作や接続先の変更より古い応答を適用しません。</summary>
    /// <returns>同じ表示・接続範囲かです。</returns>
    private bool Current(int requested, string cluster, string peer)
    {
        bool sameTarget = string.Equals(api.SelectedClusterId, cluster, StringComparison.Ordinal) && string.Equals(api.SelectedPeerId, peer, StringComparison.Ordinal);
        if (!sameTarget && requested == generation)
        {
            Available = false;
        }
        return requested == generation && sameTarget;
    }
}
