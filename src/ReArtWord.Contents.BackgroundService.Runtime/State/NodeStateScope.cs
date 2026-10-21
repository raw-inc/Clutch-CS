namespace ReArtWord.Contents.BackgroundService.Runtime.State;

/// <summary>ノード状態をPeer内部の運用保存領域から分離します。</summary>
public static class NodeStateScope
{
    private static string SharedPrefix => "workflow:";

    /// <summary>クラスタ内の全ワークフローで共有する永続状態範囲です。</summary>
    public static string Cluster => "state:cluster";

    /// <summary>クラスタ内へ複製するユーザー状態だけを判定します。</summary>
    /// <returns>共有状態範囲であるかです。</returns>
    public static bool IsShared(string scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return string.Equals(scope, Cluster, StringComparison.Ordinal)
            || scope.StartsWith(SharedPrefix, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(scope[SharedPrefix.Length..]);
    }

    /// <summary>ワークフローの共有状態範囲を作ります。</summary>
    /// <returns>共有状態だけの保存範囲です。</returns>
    public static string Shared(string workflowId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        return SharedPrefix + workflowId;
    }

    /// <summary>指定Peer内のワークフロー状態範囲を作ります。</summary>
    /// <returns>内部membership等と交差しない保存範囲です。</returns>
    public static string Peer(string peerId, string workflowId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        return LocalPrefix(peerId) + workflowId;
    }

    /// <summary>共有または当該Peerのノード状態範囲だけを許可します。</summary>
    /// <returns>ノード状態として使用できるかです。</returns>
    public static bool IsNodeScope(string scope, string peerId) => string.Equals(scope, Cluster, StringComparison.Ordinal) || TryGetWorkflowId(scope, peerId, out _);

    /// <summary>設定の共有範囲を、内部運用領域と交差しない保存名へ解決します。</summary>
    /// <returns>クラスタ、ワークフロー共有、またはPeer内の状態範囲です。</returns>
    public static string Resolve(string? scope, string peerId, string workflowId) => scope switch
    {
        "cluster" => Cluster,
        "peer" => Peer(peerId, workflowId),
        null or "workflow" => Shared(workflowId),
        _ => throw new ArgumentException("状態の保存範囲が不正です。", nameof(scope)),
    };

    /// <summary>正規の状態範囲から所有ワークフローを読みます。</summary>
    /// <returns>空でない所有ワークフローを確認できるかです。</returns>
    public static bool TryGetWorkflowId(string scope, string peerId, out string? workflowId)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(peerId);
        string prefix = SharedPrefix;
        if (!scope.StartsWith(prefix, StringComparison.Ordinal))
        {
            prefix = LocalPrefix(peerId);
        }
        workflowId = null;
        if (scope.StartsWith(prefix, StringComparison.Ordinal))
        {
            workflowId = scope[prefix.Length..];
        }
        return !string.IsNullOrWhiteSpace(workflowId);
    }

    /// <summary>ローカルノード状態の予約接頭辞を組み立てます。</summary>
    /// <returns>内部保存と分離した接頭辞です。</returns>
    private static string LocalPrefix(string peerId) => "peer:" + peerId + ":workflow:";
}
