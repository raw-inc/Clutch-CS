using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;

/// <summary>選択ノードと接する接続線だけを変更し、対象外の現在値を保ちます。</summary>
internal static class SnapshotNodeSelection
{
    /// <summary>三者比較の競合パスを選択ノードの範囲へ対応付けます。</summary>
    /// <returns>当該競合も調停対象か。</returns>
    public static bool ContainsConflict(string path, IReadOnlyList<string>? nodeIds)
    {
        if (nodeIds is null || string.IsNullOrEmpty(path))
        {
            return true;
        }
        string[] parts = path.Split('/');
        if (parts.Length < 3)
        {
            return false;
        }
        string id = parts[2].Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
        string nodes = "nodes";
        if (string.Equals(parts[1], nodes, StringComparison.Ordinal))
        {
            return nodeIds.Contains(id, StringComparer.Ordinal);
        }
        string wires = "wires";
        if (!string.Equals(parts[1], wires, StringComparison.Ordinal))
        {
            return false;
        }
        string[]? endpoints = JsonSerializer.Deserialize<string[]>(id);
        return endpoints is not null && endpoints.Length == 4 && (nodeIds.Contains(endpoints[0], StringComparer.Ordinal) || nodeIds.Contains(endpoints[2], StringComparer.Ordinal));
    }

    /// <summary>取得時に明示した範囲から復元範囲を拡大しません。</summary>
    /// <returns>候補が要求範囲を含むか。</returns>
    public static bool Covers(SnapshotRecord record, SnapshotTarget target)
    {
        if (!string.Equals(record.TargetId, target.Id, StringComparison.Ordinal) || !string.Equals(record.PeerId, target.PeerId, StringComparison.Ordinal) || record.Kind != target.Kind)
        {
            return false;
        }
        return record.NodeIds is null || (target.NodeIds is not null && target.NodeIds.All(id => record.NodeIds.Contains(id, StringComparer.Ordinal)));
    }

    /// <summary>選択集合と状態種別の意味を確認します。</summary>
    /// <returns>正しい全体または部分設定の指定か。</returns>
    public static bool Valid(SnapshotTarget target)
    {
        return target.NodeIds is null || (target.Kind == ESnapshotKind.Configuration && target.NodeIds.Count != 0
            && target.NodeIds.All(id => !string.IsNullOrWhiteSpace(id)) && target.NodeIds.Distinct(StringComparer.Ordinal).Count() == target.NodeIds.Count);
    }

    /// <summary>保存版と有効版をそれぞれの現在値へ部分適用します。</summary>
    /// <returns>対象外を維持した復元候補です。</returns>
    public static ConfigurationSnapshotState Merge(ConfigurationSnapshotState current, ConfigurationSnapshotState snapshot, IReadOnlyList<string>? nodeIds)
    {
        if (nodeIds is null)
        {
            return snapshot;
        }
        WorkflowDefinition? effective = null;
        if (current.Effective is not null || snapshot.Effective is not null)
        {
            WorkflowDefinition basis = current.Effective ?? current.Saved.Definition with { Nodes = [], Wires = [], Enabled = snapshot.Effective!.Enabled };
            WorkflowDefinition desired = snapshot.Effective ?? basis with { Nodes = [], Wires = [] };
            effective = WorkflowSnapshotSelection.Patch(basis, desired, nodeIds);
        }
        return new(current.Saved with { Definition = WorkflowSnapshotSelection.Patch(current.Saved.Definition, snapshot.Saved.Definition, nodeIds) }, effective);
    }

    /// <summary>選択範囲だけを事後比較や編集保護へ使用します。</summary>
    /// <returns>設定属性のうち対象範囲の内容ハッシュです。</returns>
    public static string Hash(WorkflowDefinition? definition, IReadOnlyList<string>? nodeIds)
    {
        if (nodeIds is null)
        {
            return ContractJson.Hash(definition);
        }
        HashSet<string> selected = new(nodeIds, StringComparer.Ordinal);
        return ContractJson.Hash(new
        {
            nodes = (definition?.Nodes ?? []).Where(node => selected.Contains(node.Id)).OrderBy(node => node.Id, StringComparer.Ordinal),
            wires = (definition?.Wires ?? []).Where(wire => Touches(wire, selected)).OrderBy(ContractJson.Hash, StringComparer.Ordinal),
        });
    }

    /// <summary>境界をまたぐ接続も選択ノードの編集範囲に含めます。</summary>
    /// <returns>接続端点が選択されているか。</returns>
    private static bool Touches(WireDefinition wire, HashSet<string> selected) => selected.Contains(wire.FromNode) || selected.Contains(wire.ToNode);
}
