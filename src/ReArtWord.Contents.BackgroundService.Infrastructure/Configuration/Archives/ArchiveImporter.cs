using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Archives;

/// <summary>一括復元を一つの共有取引へまとめ、固定参加ピアの実有効状態まで確認します。</summary>
internal sealed class ArchiveImporter(IDurableRepository repository, PeerCluster cluster, WorkflowStore workflows,
    ClusterConfigurationStore operational, WorkflowLogService log)
{
    private readonly ClusterArchiveState state = new(cluster, workflows, operational);

    /// <summary>復元後の実状態を各参加ピアで読み取る検査を登録します。</summary>
    internal void Register()
    {
        cluster.RegisterInspection("archive.fingerprint", async (_, token) => new(EOutcome.Succeeded,
            NodeValue.FromObject(ClusterArchiveState.Fingerprint(await state.CaptureAsync(cancellationToken: token).ConfigureAwait(false)))));
    }

    /// <summary>アーカイブ全体を検証し、追加・上書き・削除と確認値を固定します。</summary>
    /// <returns>明示開始に使用する計画です。</returns>
    internal async Task<ArchiveImportPlan> PreviewAsync(ArchivePreviewRequest request, ArchiveWorkspace workspace, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(request.Mode))
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }
        ArchiveTransfer file = await workspace.VerifyAsync(request.OperationId, cancellationToken).ConfigureAwait(false);
        (ClusterArchiveConfiguration incoming, long logs) = await ClusterArchiveFile.InspectAsync(workspace.FileFor(request.OperationId, "archive.zip"), cancellationToken).ConfigureAwait(false);
        PeerInfo[] members = await ReadyMembersAsync(cancellationToken).ConfigureAwait(false);
        ClusterArchiveConfiguration before = await state.CaptureAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        ClusterArchiveConfiguration desired = ClusterArchiveState.Desired(before, incoming, request.Mode);
        IReadOnlyList<ValidationIssue> issues = await state.ValidateAsync(incoming, desired, cancellationToken).ConfigureAwait(false);
        if (issues.Count != 0)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, issues.Select(issue => issue.Code + ": " + issue.Message)));
        }
        HashSet<string> current = before.Workflows.Select(item => item.Saved.Definition.Id).ToHashSet(StringComparer.Ordinal);
        string[] imported = incoming.Workflows.Select(item => item.Saved.Definition.Id).Order(StringComparer.Ordinal).ToArray();
        string[] deleted = [];
        if (request.Mode == EArchiveImportMode.Replace)
        {
            deleted = current.Except(imported, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        }
        string confirmation = ContractJson.Hash(new { file.Sha256, Before = ClusterArchiveState.Fingerprint(before), request.Mode,
            Members = members.Select(member => new { member.PeerId, member.InstanceId }) });
        ArchiveImportPreview preview = new(request.OperationId, confirmation, request.Mode, incoming.ClusterId, incoming.PeerId,
            incoming.CapturedAt, imported.Where(id => !current.Contains(id)).ToArray(), imported.Where(current.Contains).ToArray(), deleted, logs);
        return new(preview, file, before, incoming, members);
    }

    /// <summary>実行前の変更を拒否し、確認済みの全設定を原子的に共有します。</summary>
    /// <returns>共有保存と実反映、ログ取込みを区別した結果です。</returns>
    internal async Task<ArchiveOperationResult> ApplyAsync(ArchiveImportPlan plan, ArchiveWorkspace workspace,
        Func<ArchiveOperationResult, CancellationToken, Task> save, CancellationToken cancellationToken = default)
    {
        Guid id = plan.Preview.OperationId;
        ArchiveTransfer file = await workspace.VerifyAsync(id, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(file.Sha256, plan.File.Sha256, StringComparison.Ordinal)
            || !string.Equals(ClusterArchiveState.Fingerprint(await state.CaptureAsync(cancellationToken: cancellationToken).ConfigureAwait(false)), ClusterArchiveState.Fingerprint(plan.Before), StringComparison.Ordinal))
        {
            return new(id, "preflight", true, EOutcome.NotExecuted, "Archive.PreviewChanged");
        }
        PeerInfo[] current = await ReadyMembersAsync(cancellationToken).ConfigureAwait(false);
        if (!current.Select(peer => peer.InstanceId).SequenceEqual(plan.Members.Select(peer => peer.InstanceId)))
        {
            return new(id, "preflight", true, EOutcome.NotExecuted, "Archive.ParticipantsChanged");
        }
        SnapshotEditProtection protection = new(repository, cluster);
        SnapshotEditLock[] desired = plan.Before.Workflows.Concat(plan.Incoming.Workflows).Select(item => item.Saved.Definition.Id).Distinct(StringComparer.Ordinal)
            .Select(workflow => new SnapshotEditLock(id, workflow, null)).Append(new(id, ClusterConfigurationStore.Id, null, true)).ToArray();
        await save(new(id, "protecting", false, EOutcome.Unknown), cancellationToken).ConfigureAwait(false);
        ConfigurationPhaseResult locked = await protection.AcquireAsync(id, desired, cancellationToken).ConfigureAwait(false);
        if (locked.Outcome != EOutcome.Succeeded)
        {
            return new(id, "protecting", true, locked.Outcome, locked.Reason);
        }
        if (!string.Equals(ClusterArchiveState.Fingerprint(await state.CaptureAsync(cancellationToken: cancellationToken).ConfigureAwait(false)), ClusterArchiveState.Fingerprint(plan.Before), StringComparison.Ordinal))
        {
            ConfigurationPhaseResult unlocked = await protection.ReleaseAsync(id, cancellationToken).ConfigureAwait(false);
            if (unlocked.Outcome != EOutcome.Succeeded)
            {
                return new(id, "protecting", true, EOutcome.Unknown, unlocked.Reason);
            }
            return new(id, "preflight", true, EOutcome.NotExecuted, "Archive.PreviewChanged");
        }
        IReadOnlyList<DurableMutation> mutations = await new ClusterArchiveMutations(repository, cluster).CreateAsync(id, plan.Before, plan.Incoming, plan.Preview.Mode, cancellationToken).ConfigureAwait(false);
        await save(new(id, "applying", false, EOutcome.Unknown), cancellationToken).ConfigureAwait(false);
        DurableReceipt receipt = await cluster.ReplicateFixedAsync(CommitId(id), mutations, plan.Members, cancellationToken).ConfigureAwait(false);
        if (receipt.Outcome != EOutcome.Succeeded)
        {
            if (receipt.Outcome != EOutcome.Unknown)
            {
                ConfigurationPhaseResult unlocked = await protection.ReleaseAsync(id, cancellationToken).ConfigureAwait(false);
                if (unlocked.Outcome != EOutcome.Succeeded)
                {
                    return new(id, "releasing", true, EOutcome.Unknown, unlocked.Reason, Steps: [new("configuration", receipt.Outcome, receipt.Reason)]);
                }
            }
            return new(id, "configuration", true, receipt.Outcome, receipt.Reason, Steps: [new("configuration", receipt.Outcome, receipt.Reason)]);
        }
        await save(new(id, "confirming", false, EOutcome.Unknown, Steps: [new("configuration", EOutcome.Succeeded)]), cancellationToken).ConfigureAwait(false);
        if (!await ConfirmAsync(plan, cancellationToken).ConfigureAwait(false))
        {
            return new(id, "confirming", true, EOutcome.Unknown, "Archive.ActivationUnconfirmed", Steps: [new("configuration", EOutcome.Succeeded), new("activation", EOutcome.Unknown)]);
        }
        ConfigurationPhaseResult released = await protection.ReleaseAsync(id, cancellationToken).ConfigureAwait(false);
        if (released.Outcome != EOutcome.Succeeded)
        {
            return new(id, "releasing", true, EOutcome.Unknown, released.Reason, Steps: [new("configuration", EOutcome.Succeeded), new("activation", EOutcome.Succeeded)]);
        }
        List<ArchiveStepResult> steps = [new("configuration", EOutcome.Succeeded), new("activation", EOutcome.Succeeded)];
        if (plan.Incoming.IncludesLogs)
        {
            await save(new(id, "logs", false, EOutcome.Unknown, Steps: [.. steps, new("logs", EOutcome.Unknown)]), cancellationToken).ConfigureAwait(false);
            await foreach (IReadOnlyList<Contracts.Observability.Logging.WorkflowLogEntry> page in ClusterArchiveFile.ReadLogsAsync(workspace.FileFor(id, "archive.zip"), cancellationToken).ConfigureAwait(false))
            {
                await log.RestoreArchiveAsync(page, cancellationToken).ConfigureAwait(false);
            }
            steps.Add(new("logs", EOutcome.Succeeded));
        }
        return new(id, "completed", true, EOutcome.Succeeded, Steps: steps);
    }

    /// <summary>元取引と現在状態だけを確認し、設定の再書込みやログの再取込みは行いません。</summary>
    /// <returns>既知結果、または引き続き未確認の結果です。</returns>
    internal async Task<ArchiveOperationResult> ReconcileAsync(ArchiveImportPlan plan, ArchiveOperationResult previous, CancellationToken cancellationToken = default)
    {
        Guid id = plan.Preview.OperationId;
        string protecting = "protecting";
        if (string.Equals(previous.Stage, protecting, StringComparison.Ordinal))
        {
            DurableReceipt? reservation = await cluster.ConfirmReplicationAsync(WorkflowStore.PhaseId(id, "edit-lock"), plan.Members, cancellationToken).ConfigureAwait(false);
            if (reservation?.Outcome == EOutcome.Succeeded)
            {
                ConfigurationPhaseResult unlocked = await new SnapshotEditProtection(repository, cluster).ReleaseAsync(id, cancellationToken).ConfigureAwait(false);
                if (unlocked.Outcome == EOutcome.Succeeded)
                {
                    return new(id, "preflight", true, EOutcome.NotExecuted, "Archive.StoppedBeforeApply");
                }
            }
            return previous with { Completed = true, Outcome = EOutcome.Unknown, Reason = "Archive.ProtectionUnconfirmed" };
        }
        DurableReceipt? receipt = await cluster.ConfirmReplicationAsync(CommitId(id), plan.Members, cancellationToken).ConfigureAwait(false);
        if (receipt is { Outcome: not EOutcome.Succeeded and not EOutcome.Unknown })
        {
            ConfigurationPhaseResult unlocked = await new SnapshotEditProtection(repository, cluster).ReleaseAsync(id, cancellationToken).ConfigureAwait(false);
            if (unlocked.Outcome == EOutcome.Succeeded)
            {
                return new(id, "configuration", true, receipt.Outcome, receipt.Reason, Steps: [new("configuration", receipt.Outcome, receipt.Reason)]);
            }
            return previous with { Completed = true, Outcome = EOutcome.Unknown, Reason = unlocked.Reason };
        }
        if (receipt?.Outcome != EOutcome.Succeeded || !await ConfirmAsync(plan, cancellationToken).ConfigureAwait(false))
        {
            return previous with { Completed = true, Outcome = EOutcome.Unknown, Reason = "Archive.ConfigurationUnconfirmed" };
        }
        ConfigurationPhaseResult released = await new SnapshotEditProtection(repository, cluster).ReleaseAsync(id, cancellationToken).ConfigureAwait(false);
        if (released.Outcome != EOutcome.Succeeded)
        {
            return previous with { Completed = true, Outcome = EOutcome.Unknown, Reason = released.Reason };
        }
        bool logsConfirmed = !plan.Incoming.IncludesLogs || previous.Steps?.Any(step => step.Target is "logs" && step.Outcome == EOutcome.Succeeded) == true;
        if (!logsConfirmed)
        {
            return new(id, "logs", true, EOutcome.Unknown, "Archive.LogRestoreUnconfirmed", Steps: [new("configuration", EOutcome.Succeeded), new("activation", EOutcome.Succeeded), new("logs", EOutcome.Unknown)]);
        }
        return new(id, "completed", true, EOutcome.Succeeded, Steps: previous.Steps);
    }

    /// <summary>設定の再反映は通常処理に任せ、全固定実体の実有効状態を読み取ります。</summary>
    /// <returns>同じ内容と有効状態まで確認できた場合だけtrueです。</returns>
    private async Task<bool> ConfirmAsync(ArchiveImportPlan plan, CancellationToken cancellationToken = default)
    {
        string expected = ClusterArchiveState.Fingerprint(ClusterArchiveState.Desired(plan.Before, plan.Incoming, plan.Preview.Mode));
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            while (!timeout.IsCancellationRequested)
            {
                await operational.ReconcileAsync(timeout.Token).ConfigureAwait(false);
                await workflows.ReconcileActivationAsync(timeout.Token).ConfigureAwait(false);
                bool matched = true;
                foreach (PeerInfo member in plan.Members)
                {
                    ClusterResponse response = await cluster.InspectAsync(new(Guid.NewGuid(), "archive.fingerprint", member.PeerId, member.InstanceId,
                        DateTimeOffset.UtcNow.AddSeconds(3), NodeValue.Null), timeout.Token).ConfigureAwait(false);
                    matched &= response.Outcome == EOutcome.Succeeded && string.Equals(response.Value?.GetString(), expected, StringComparison.Ordinal);
                }
                if (matched)
                {
                    return true;
                }
                await Task.Delay(250, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
        return false;
    }

    /// <summary>参加実体と設定の競合を確認し、未解消の候補を上書きしません。</summary>
    /// <returns>開始時に固定するReady集合です。</returns>
    private async Task<PeerInfo[]> ReadyMembersAsync(CancellationToken cancellationToken = default)
    {
        PeerInfo[] members = cluster.GetPeers().OrderBy(peer => peer.PeerId, StringComparer.Ordinal).ToArray();
        if (cluster.IsDetached || members.Length == 0 || members.Any(peer => peer.Status != EPeerStatus.Ready))
        {
            throw new InvalidOperationException("All cluster members must be Ready before importing an archive.");
        }
        foreach (string scope in new[] { "workflows", "workflow.activation", ClusterConfigurationStore.SavedScope, ClusterConfigurationStore.ActivationScope })
        {
            if ((await repository.GetConflictsAsync(scope, cancellationToken).ConfigureAwait(false)).Count != 0)
            {
                throw new InvalidOperationException("Resolve configuration conflicts before importing an archive.");
            }
        }
        return members;
    }

    /// <summary>元操作と一括確定を一意に対応させます。</summary>
    /// <returns>共有取引の操作IDです。</returns>
    private static Guid CommitId(Guid operation) => WorkflowStore.PhaseId(operation, "archive.commit");
}
