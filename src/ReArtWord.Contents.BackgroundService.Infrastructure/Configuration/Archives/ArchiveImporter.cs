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

/// <summary>設定を一括復元し、参加ピアへの反映を確認してからログを取り込みます。</summary>
internal sealed class ArchiveImporter(IDurableRepository repository, PeerCluster cluster, WorkflowStore workflows,
    ClusterConfigurationStore operational, WorkflowLogService log)
{
    private readonly ClusterArchiveState state = new(cluster, workflows, operational);

    /// <summary>各ピアの保存版と有効版を比較するため、設定のハッシュを返す照会を登録します。</summary>
    internal void Register()
    {
        cluster.RegisterInspection("archive.fingerprint", async (_, token) => new(EOutcome.Succeeded,
            NodeValue.FromObject(ClusterArchiveState.Fingerprint(await state.CaptureAsync(cancellationToken: token).ConfigureAwait(false)))));
    }

    /// <summary>アーカイブを検証し、変更されるWFと確認トークンを含む復元計画を作成します。</summary>
    /// <returns>復元の開始時に使用する計画。</returns>
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

    /// <summary>事前確認後の変更がないことを確かめ、設定を一つのトランザクションで復元します。</summary>
    /// <returns>設定の保存・反映・ログ取り込みの各結果。</returns>
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

    /// <summary>中断した復元の結果を再確認します。設定やログは再度書き込みません。</summary>
    /// <returns>再確認後の復元結果。</returns>
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

    /// <summary>復元計画に記録した全ピアへ、設定が反映されたか確認します。</summary>
    /// <returns>保存版と有効版が復元計画に一致した場合はtrue。</returns>
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

    /// <summary>全参加ピアがReadyで、設定の競合がないことを確認します。</summary>
    /// <returns>復元に参加するピアの一覧。</returns>
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

    /// <summary>復元の操作IDから、設定保存に使用するトランザクションIDを生成します。</summary>
    /// <returns>設定保存のトランザクションID。</returns>
    private static Guid CommitId(Guid operation) => WorkflowStore.PhaseId(operation, "archive.commit");
}
