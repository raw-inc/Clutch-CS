using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Runtime.State;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;

/// <summary>設定の整合群を同じ用途にまとめ、物理媒体を解決します。</summary>
internal sealed class RepositoryRoutes
{
    private static readonly string[] configurationPrefixes = ["workflow.history:", "snapshot.", "cluster.operational", "trace:", "cluster:configuration-", "cluster:snapshot-"];
    private static readonly string[] configurationScopes = ["workflows", "workflow.activation", "snapshots", "secrets", "configuration"];
    private static readonly string[] runtimePrefixes = ["workflow:", "execution:", "file:", "cluster:replication-", "cluster:operations"];
    private static readonly string[] runtimeScopes = ["manual-runs", "updates", NodeStateScope.Cluster];
    private static readonly string[] processNames = ["processDefinitions", "processStates", "processSchedules", "processGroups", "processGroupStates", "processGroupMembers"];
    private static readonly string[] diagnosticNames = ["resources", "log-index", "log-forward"];
    private static readonly string[] configurationNames = ["effective", "operational-effective", "trace-operations", "snapshot-draft-requests", "detached-workflows", "detached-snapshots"];

    private readonly PersistenceOptions options;
    private readonly string peerPrefix;

    /// <summary>検証済みの不変設定と媒体実体を受け取ります。</summary>
    internal RepositoryRoutes(string peerId, PersistenceOptions options, IReadOnlyDictionary<EStorageBackend, IDurableRepository> stores)
    {
        this.options = options;
        peerPrefix = "peer:" + peerId + ":";
        Stores = stores;
        EStorageBackend configuration = options.Purposes?.GetValueOrDefault(EStoragePurpose.Configuration, options.DefaultBackend) ?? options.DefaultBackend;
        EStorageBackend runtime = options.Purposes?.GetValueOrDefault(EStoragePurpose.Runtime, options.DefaultBackend) ?? options.DefaultBackend;
        SharedStores = new[] { options.DefaultBackend, configuration, runtime }.Distinct().Select(backend => stores[backend]).ToArray();
    }

    /// <summary>同じバックエンドを重複して作成しない媒体集合です。</summary>
    internal IReadOnlyDictionary<EStorageBackend, IDurableRepository> Stores
    {
        get;
    }

    /// <summary>共有スコープを持ち得る媒体だけを一実体につき一度確認します。</summary>
    internal IReadOnlyList<IDurableRepository> SharedStores
    {
        get;
    }

    /// <summary>ローカルrouting情報と未分類用途の保存先です。</summary>
    internal EStorageBackend Default => options.DefaultBackend;

    /// <summary>用途の設定値または既存defaultを取得します。</summary>
    /// <returns>このPeerの物理媒体です。</returns>
    internal EStorageBackend Backend(string scope)
    {
        EStoragePurpose? purpose = Purpose(scope);
        if (purpose.HasValue && options.Purposes?.TryGetValue(purpose.Value, out EStorageBackend configured) == true)
        {
            return configured;
        }
        return Default;
    }

    /// <summary>一取引が一つの原子媒体で完了できるか確認します。</summary>
    /// <returns>単一媒体、または跨媒体のnullです。</returns>
    internal EStorageBackend? Select(IEnumerable<string> scopes)
    {
        EStorageBackend[] selected = scopes.Select(Backend).Distinct().ToArray();
        if (selected.Length == 0)
        {
            return Default;
        }
        if (selected.Length == 1)
        {
            return selected[0];
        }
        return null;
    }

    /// <summary>共有スコープとPeerローカルスコープを同じ用途意味に分類します。</summary>
    /// <returns>既存defaultを使う未分類ではnullです。</returns>
    private EStoragePurpose? Purpose(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        if (configurationScopes.Contains(scope, StringComparer.Ordinal) || configurationPrefixes.Any(prefix => scope.StartsWith(prefix, StringComparison.Ordinal)))
        {
            return EStoragePurpose.Configuration;
        }
        if (runtimeScopes.Contains(scope, StringComparer.Ordinal) || runtimePrefixes.Any(prefix => scope.StartsWith(prefix, StringComparison.Ordinal)))
        {
            return EStoragePurpose.Runtime;
        }
        if (scope.StartsWith(peerPrefix, StringComparison.Ordinal))
        {
            string name = scope[peerPrefix.Length..];
            if (processNames.Contains(name, StringComparer.Ordinal))
            {
                return EStoragePurpose.Processes;
            }
            if (diagnosticNames.Contains(name, StringComparer.Ordinal))
            {
                return EStoragePurpose.Diagnostics;
            }
            string detachedHistory = "detached-history:";
            if (configurationNames.Contains(name, StringComparer.Ordinal) || name.StartsWith(detachedHistory, StringComparison.Ordinal))
            {
                return EStoragePurpose.Configuration;
            }
            return EStoragePurpose.Runtime;
        }
        return null;
    }
}
