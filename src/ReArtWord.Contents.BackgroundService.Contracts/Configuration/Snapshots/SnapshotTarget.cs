namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;

/// <summary>復元範囲に含める実対象です。</summary>
/// <param name="Id">ワークフローまたはruntime participant識別子。</param>
/// <param name="PeerId">対象Peer。</param>
/// <param name="Kind">状態種類。</param>
/// <param name="NodeIds">設定スナップショットの選択ノード集合。nullはワークフロー全体です。</param>
public sealed record SnapshotTarget(string Id, string PeerId, ESnapshotKind Kind, IReadOnlyList<string>? NodeIds = null)
{
    /// <summary>配送・再読込後も同じ選択集合を同一対象として扱います。</summary>
    /// <returns>対象と選択ノード集合が一致するか。</returns>
    public bool Equals(SnapshotTarget? other)
    {
        if (other is null || !string.Equals(Id, other.Id, StringComparison.Ordinal) || !string.Equals(PeerId, other.PeerId, StringComparison.Ordinal) || Kind != other.Kind)
        {
            return false;
        }
        if (NodeIds is null || other.NodeIds is null)
        {
            return NodeIds is null && other.NodeIds is null;
        }
        return NodeIds.Order(StringComparer.Ordinal).SequenceEqual(other.NodeIds.Order(StringComparer.Ordinal), StringComparer.Ordinal);
    }

    /// <summary>順序によらない選択集合のハッシュです。</summary>
    /// <returns>辞書で使用するハッシュです。</returns>
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Id, StringComparer.Ordinal);
        hash.Add(PeerId, StringComparer.Ordinal);
        hash.Add(Kind);
        hash.Add(NodeIds is null);
        foreach (string id in (NodeIds ?? []).Order(StringComparer.Ordinal))
        {
            hash.Add(id, StringComparer.Ordinal);
        }
        return hash.ToHashCode();
    }
}
