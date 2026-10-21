using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.Concurrency;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;

/// <summary>開いている図の制限ノードだけをまとめて照会し、ノードごとのポーリングを作りません。</summary>
internal sealed class EditorConcurrencyQueues(ManagementApi api)
{
    private readonly Dictionary<string, WorkflowConcurrencySnapshot> snapshots = new(StringComparer.Ordinal);
    private (string? Cluster, string? Peer, string? Workflow) current;
    private DateTimeOffset next;
    private int generation;
    private bool reading;

    /// <summary>待機表示が変化したときだけキャンバスを更新する版です。</summary>
    internal int Version
    {
        get; private set;
    }

    /// <summary>別の図の同名ノードへ状態を渡しません。</summary>
    /// <returns>現在の図に属する状態、または未確認のnullです。</returns>
    internal WorkflowConcurrencySnapshot? Find(string? cluster, string workflow, string node)
        => string.Equals(current.Cluster, cluster, StringComparison.Ordinal) && string.Equals(current.Workflow, workflow, StringComparison.Ordinal) ? snapshots.GetValueOrDefault(node)
                                                                                                                                                    : null;

    /// <summary>画面の照会周期内で一回だけ、表示対象を最大100件ずつ取得します。</summary>
    /// <returns>世代が一致した現在表示の反映です。</returns>
    internal async Task RefreshAsync(string cluster, string peer, WorkflowDefinition definition, CancellationToken cancellationToken = default)
    {
        (string Cluster, string Peer, string Workflow) requested = (cluster, peer, definition.Id);
        if (current != requested)
        {
            current = requested;
            generation++;
            snapshots.Clear();
            next = DateTimeOffset.MinValue;
            Version++;
        }
        if (reading || api.UtcNow < next)
        {
            return;
        }
        WorkflowConcurrencyTarget[] targets = definition.Nodes.Where(node => ConcurrencyNodeTypes.IsStart(node.Type))
            .Select(node => new WorkflowConcurrencyTarget(definition.Id, node.Id)).ToArray();
        if (targets.Length == 0)
        {
            if (snapshots.Count != 0)
            {
                snapshots.Clear();
                Version++;
            }
            return;
        }
        reading = true;
        int request = generation;
        next = api.UtcNow.AddSeconds(1);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            List<WorkflowConcurrencySnapshot> response = [];
            foreach (WorkflowConcurrencyTarget[] batch in targets.Chunk(100))
            {
                response.AddRange(await api.PostForPeerAsync<List<WorkflowConcurrencySnapshot>>(
                    "/api/workflows/concurrency/status", batch, peer, cluster, cancellationToken: timeout.Token).ConfigureAwait(true) ?? []);
            }
            if (request != generation)
            {
                return;
            }
            foreach (WorkflowConcurrencyTarget target in targets)
            {
                WorkflowConcurrencySnapshot value = response.FirstOrDefault(item => string.Equals(item.WorkflowId, definition.Id, StringComparison.Ordinal)
                    && string.Equals(item.NodeId, target.NodeId, StringComparison.Ordinal)) ?? new(definition.Id, target.NodeId, 0, 0, [], api.UtcNow, "concurrency.unavailable");
                WorkflowConcurrencySnapshot? old = snapshots.GetValueOrDefault(value.NodeId);
                if (old is null || old.ActiveCount != value.ActiveCount || old.WaitingCount != value.WaitingCount || value.WaitingCount != 0 || !string.Equals(old.Problem, value.Problem, StringComparison.Ordinal))
                {
                    Version++;
                }
                snapshots[value.NodeId] = value;
            }
            foreach (string removed in snapshots.Keys.Where(node => !targets.Any(target => string.Equals(target.NodeId, node, StringComparison.Ordinal))).ToArray())
            {
                snapshots.Remove(removed);
                Version++;
            }
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException)
        {
            if (request == generation)
            {
                foreach (WorkflowConcurrencyTarget target in targets)
                {
                    snapshots[target.NodeId] = new(definition.Id, target.NodeId, 0, 0, [], api.UtcNow, "concurrency.unavailable");
                }
                Version++;
            }
        }
        finally
        {
            reading = false;
        }
    }

    /// <summary>接続先の観測がない間は古い一覧を現在値として表示しません。</summary>
    internal void Reset()
    {
        generation++;
        if (snapshots.Count != 0)
        {
            snapshots.Clear();
            Version++;
        }
        next = DateTimeOffset.MinValue;
    }
}
