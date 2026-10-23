using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Archives;

/// <summary>保存版と実有効版の読み取り、復元候補の検証、確認画面の差分をまとめます。</summary>
internal sealed class ClusterArchiveState(PeerCluster cluster, WorkflowStore workflows, ClusterConfigurationStore operational)
{
    /// <summary>取得中に設定が変わった場合は混在したバックアップを作りません。</summary>
    /// <returns>同じ内容を連続して確認できた設定です。</returns>
    public async Task<ClusterArchiveConfiguration> CaptureAsync(bool includeLogs = false, CancellationToken cancellationToken = default)
    {
        ClusterArchiveConfiguration before = await ReadAsync(includeLogs, cancellationToken).ConfigureAwait(false);
        ClusterArchiveConfiguration after = await ReadAsync(includeLogs, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(Fingerprint(before), Fingerprint(after), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Configuration changed while preparing the archive.");
        }
        return after;
    }

    /// <summary>内容の版、適用方針、実有効版も含めて変更前に検証します。</summary>
    /// <returns>取り込みを妨げる問題です。</returns>
    internal async Task<IReadOnlyList<ValidationIssue>> ValidateAsync(ClusterArchiveConfiguration configuration, ClusterArchiveConfiguration? desired = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        List<ValidationIssue> issues = [];
        if (configuration.FormatVersion != 1 || configuration.Workflows is null || configuration.Operational is null)
        {
            return [new("Archive.Format", "対応していない形式、または設定が欠けたアーカイブです。")];
        }
        issues.AddRange(await operational.SnapshotParticipant.ValidateAsync(Contracts.Nodes.NodeValue.FromObject(configuration.Operational), cancellationToken).ConfigureAwait(false));
        if (configuration.Operational.Saved is null || configuration.Operational.Effective is null
            || 0 < configuration.Operational.Saved.UnresolvedFields?.Count || 0 < configuration.Operational.Effective.UnresolvedFields?.Count)
        {
            issues.Add(new("Archive.OperationalUnconfirmed", "共通設定の保存版・有効版が未確認、または競合が残っています。"));
        }
        Dictionary<string, Contracts.Workflow.WorkflowDefinition> recipes = (desired ?? configuration).Workflows.Where(item => item?.Effective?.Nodes is not null)
            .Select(item => item.Effective!).Where(Contracts.Workflow.SubworkflowInterface.IsSubworkflow).GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (ArchivedWorkflow item in (desired ?? configuration).Workflows)
        {
            if (item?.Saved?.Definition is null)
            {
                issues.Add(new("Archive.WorkflowMissing", "ワークフローの保存版がありません。"));
                continue;
            }
            StoredWorkflow saved = item.Saved;
            if (!ids.Add(saved.Definition.Id) || saved.LocalOnly || !Enum.IsDefined(saved.ActivationPolicy)
                || !string.Equals(saved.Revision, ContractJson.Hash(saved.Definition), StringComparison.Ordinal)
                || string.Equals(saved.Definition.Id, ClusterConfigurationStore.Id, StringComparison.Ordinal))
            {
                issues.Add(new("Archive.WorkflowIdentity", "ワークフローの識別子、版、保存状態が不正です。"));
            }
            issues.AddRange(workflows.Validate(saved.Definition, recipes));
            if (item.Effective is not null)
            {
                if (!string.Equals(saved.Definition.Id, item.Effective.Id, StringComparison.Ordinal))
                {
                    issues.Add(new("Archive.EffectiveIdentity", "保存版と有効版のワークフロー識別子が一致しません。"));
                }
                issues.AddRange(workflows.Validate(item.Effective, recipes));
            }
        }
        return issues;
    }

    /// <summary>取得時刻や保存日時に依存せず、実際の設定内容と有効状態を照合します。</summary>
    /// <returns>内容確認に使うSHA-256です。</returns>
    public static string Fingerprint(ClusterArchiveConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return ContractJson.Hash(new
        {
            Saved = configuration.Operational.Saved?.Settings,
            Effective = configuration.Operational.Effective?.Settings,
            SavedUnresolved = configuration.Operational.Saved?.UnresolvedFields,
            EffectiveUnresolved = configuration.Operational.Effective?.UnresolvedFields,
            configuration.Operational.SavedActivationPolicy,
            Workflows = configuration.Workflows.OrderBy(item => item.Saved.Definition.Id, StringComparer.Ordinal)
                .Select(item => new { item.Saved.Definition, item.Saved.ActivationPolicy, item.Effective }),
        });
    }

    /// <summary>残す定義と取り込む定義を固定し、開始後に対象を増やしません。</summary>
    /// <returns>全参加ピアで確認する復元後の設定です。</returns>
    public static ClusterArchiveConfiguration Desired(ClusterArchiveConfiguration current, ClusterArchiveConfiguration incoming, EArchiveImportMode mode)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(incoming);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        IEnumerable<ArchivedWorkflow> retained = [];
        if (mode == EArchiveImportMode.Merge)
        {
            HashSet<string> imported = incoming.Workflows.Select(item => item.Saved.Definition.Id).ToHashSet(StringComparer.Ordinal);
            retained = current.Workflows.Where(item => !imported.Contains(item.Saved.Definition.Id));
        }
        return incoming with
        {
            ClusterId = current.ClusterId, PeerId = current.PeerId,
            Workflows = incoming.Workflows.Concat(retained).OrderBy(item => item.Saved.Definition.Id, StringComparer.Ordinal).ToArray(),
        };
    }

    /// <summary>実有効版を保存版から推測せず、現在の実行計画から取得します。</summary>
    /// <returns>一回の読取り結果です。</returns>
    private async Task<ClusterArchiveConfiguration> ReadAsync(bool includeLogs, CancellationToken cancellationToken = default)
    {
        List<ArchivedWorkflow> definitions = [];
        foreach (StoredWorkflow workflow in await workflows.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            definitions.Add(new(workflow, workflows.GetEffective?.Invoke(workflow.Definition.Id)?.Definition));
        }
        ClusterOperationalState common = await operational.GetAsync(cancellationToken).ConfigureAwait(false);
        return new(1, DateTimeOffset.UtcNow, cluster.LocalClusterId, cluster.LocalPeerId, common,
            definitions.OrderBy(item => item.Saved.Definition.Id, StringComparer.Ordinal).ToArray(), includeLogs);
    }
}
