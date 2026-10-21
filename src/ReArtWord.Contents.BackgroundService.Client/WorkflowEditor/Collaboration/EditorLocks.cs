using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Collaboration;

/// <summary>復元対象だけを編集から保護し、未確認時に既知の保護を解除しません。</summary>
internal sealed class EditorLocks
{
    private readonly List<SnapshotEditLock> current = [];

    /// <summary>最後に確認した実際の復元保護です。</summary>
    internal IReadOnlyList<SnapshotEditLock> Current => current;

    /// <summary>取得できた実状態だけを反映します。</summary>
    internal void Update(IReadOnlyList<SnapshotEditLock> locks)
    {
        current.Clear();
        current.AddRange(locks.Where(item => !item.IsConfigurationParticipant));
    }

    /// <summary>全体復元中かを確認します。</summary>
    /// <returns>全体が保護されているか。</returns>
    internal bool Whole(string workflow) => Current.Any(item => string.Equals(item.WorkflowId, workflow, StringComparison.Ordinal) && item.NodeIds is null);

    /// <summary>対象ノードか、対象に接する配線の端点かを確認します。</summary>
    /// <returns>このノードが保護されているか。</returns>
    internal bool Node(string workflow, string node) => Current.Any(item => string.Equals(item.WorkflowId, workflow, StringComparison.Ordinal) && (item.NodeIds is null || item.NodeIds.Contains(node, StringComparer.Ordinal)));

    /// <summary>保護ノードと接続を変更する候補を拒否します。</summary>
    /// <returns>現在の範囲保護に抵触しないか。</returns>
    internal bool Allows(WorkflowDefinition before, WorkflowDefinition after)
    {
        foreach (SnapshotEditLock item in Current.Where(item => string.Equals(item.WorkflowId, before.Id, StringComparison.Ordinal)))
        {
            if (item.NodeIds is null)
            {
                return string.Equals(ContractJson.Hash(before), ContractJson.Hash(after), StringComparison.Ordinal);
            }
            if (!string.Equals(before.Id, after.Id, StringComparison.Ordinal))
            {
                return false;
            }
            foreach (string id in item.NodeIds)
            {
                if (!string.Equals(ContractJson.Hash(before.Nodes.FirstOrDefault(node => string.Equals(node.Id, id, StringComparison.Ordinal))), ContractJson.Hash(after.Nodes.FirstOrDefault(node => string.Equals(node.Id, id, StringComparison.Ordinal))), StringComparison.Ordinal))
                {
                    return false;
                }
            }
            IEnumerable<WireDefinition> oldWires = before.Wires.Where(wire => item.NodeIds.Contains(wire.FromNode, StringComparer.Ordinal) || item.NodeIds.Contains(wire.ToNode, StringComparer.Ordinal));
            IEnumerable<WireDefinition> newWires = after.Wires.Where(wire => item.NodeIds.Contains(wire.FromNode, StringComparer.Ordinal) || item.NodeIds.Contains(wire.ToNode, StringComparer.Ordinal));
            if (!oldWires.ToHashSet().SetEquals(newWires))
            {
                return false;
            }
        }
        return true;
    }
}
