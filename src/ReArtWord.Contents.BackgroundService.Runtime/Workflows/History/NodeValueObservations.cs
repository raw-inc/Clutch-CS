using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.History;

/// <summary>既存の状態照会へ渡す直近256ノードの成否を、出力本文なしで保持します。</summary>
public sealed class NodeValueObservations
{
    private readonly Lock gate = new();
    private readonly Dictionary<(string Workflow, string Node), NodeValueObservation> latest = [];
    private readonly Dictionary<(string Workflow, string Node, Guid Execution), NodeValueObservation> active = [];

    /// <summary>反復の全体実行等、グラフが所有する処理期間を記録します。</summary>
    public void Record(NodeValueObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        using (gate.EnterScope())
        {
            (string, string, Guid) activeKey = (observation.WorkflowId, observation.NodeId, observation.ExecutionId);
            if (observation.CompletedAt is null)
            {
                if (4096 <= active.Count && !active.ContainsKey(activeKey))
                {
                    active.Remove(active.MinBy(pair => pair.Value.StartedAt).Key);
                }
                active[activeKey] = observation;
            }
            else
            {
                active.Remove(activeKey);
            }
            (string Workflow, string Node) key = (observation.WorkflowId, observation.NodeId);
            if (observation.CompletedAt is not null && (!latest.TryGetValue(key, out NodeValueObservation? current) || current.ExecutionId != observation.ExecutionId))
            {
                return;
            }
            if (256 <= latest.Count && !latest.ContainsKey(key))
            {
                latest.Remove(latest.MinBy(pair => pair.Value.StartedAt).Key);
            }
            latest[key] = observation;
        }
    }

    /// <summary>安全・重複実行の待機を終え、実際に処理へ入る時点を記録します。</summary>
    public void Begin(RemoteExecutionRequest request, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        Record(new(request.Id, request.CausationId, request.WorkflowId, request.Revision, request.Node.Id, request.TargetPeerId, now));
    }

    /// <summary>古い実行の遅延完了で新しい実行の処理中表示を上書きしません。</summary>
    public void Complete(RemoteExecutionRequest request, NodeResult result, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        using (gate.EnterScope())
        {
            active.Remove((request.WorkflowId, request.Node.Id, request.Id));
            (string Workflow, string Node) key = (request.WorkflowId, request.Node.Id);
            if (latest.TryGetValue(key, out NodeValueObservation? current) && current.ExecutionId == request.Id)
            {
                latest[key] = current with { CompletedAt = now, Outcome = result.Outcome };
            }
        }
    }

    /// <summary>同じ状態照会で使い、追加のノード実行や出力取得を行いません。</summary>
    /// <returns>直近ノードの独立したスナップショットです。</returns>
    public IReadOnlyList<NodeValueObservation> Snapshot(string? workflowId = null, bool runningOnly = false)
    {
        using (gate.EnterScope())
        {
            IEnumerable<NodeValueObservation> values = latest.Values;
            if (runningOnly)
            {
                values = active.Values;
            }
            return values.Where(value => (workflowId is null || string.Equals(value.WorkflowId, workflowId, StringComparison.Ordinal))
                && (!runningOnly || value.CompletedAt is null)).OrderByDescending(value => value.StartedAt).ToArray();
        }
    }
}
