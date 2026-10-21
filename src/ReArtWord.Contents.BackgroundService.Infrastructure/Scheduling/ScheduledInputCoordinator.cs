using System.Collections.Concurrent;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.History;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.History;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Sources;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Scheduling;

/// <summary>確認済み未開始の予定入力だけを引き継ぎ、配送を開始した回を再実行しません。</summary>
public sealed class ScheduledInputCoordinator : IDisposable
{
    private readonly PeerCluster cluster;
    private readonly ScheduledInputStore store;
    private readonly WorkflowCompiler compiler;
    private readonly WorkflowEngine engine;
    private readonly SourceSupervisor sources;
    private readonly ConcurrentDictionary<Guid, bool> owned = new();
    private readonly ConcurrentDictionary<Guid, byte> permitted = new();
    private readonly ConcurrentDictionary<Guid, Completion> results = new();
    private readonly ConcurrentDictionary<Guid, byte> offered = new();
    private string? lastProblem;

    /// <summary>既存のSourceキューと配送直前の境界へ、予定入力の保持を接続します。</summary>
    public ScheduledInputCoordinator(PeerCluster cluster, ScheduledInputStore store, WorkflowCompiler compiler, WorkflowEngine engine, SourceSupervisor sources)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(compiler);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(sources);
        this.cluster = cluster;
        this.store = store;
        this.compiler = compiler;
        this.engine = engine;
        this.sources = sources;
        store.Reserved += Reserved;
        engine.BeforeScheduledDispatch = PermitAsync;
        engine.ScheduledResult = CompletedAsync;
        engine.ScheduledInputRejected = Rejected;
        engine.CancelScheduledInput = new ScheduledInputCancellation(cluster, store).CancelAsync;
    }

    /// <summary>予定の保持・引継ぎで最後に確認した問題。</summary>
    public string? LastProblem => Volatile.Read(ref lastProblem);

    /// <summary>この実体が管理し、まだ完了記録を確認していない予定入力の数。</summary>
    public int LocallyManagedInputs => owned.Count;

    /// <summary>実行結果を保持したまま、保存確認を待っている入力の数。</summary>
    public int UnconfirmedResults => results.Count;

    /// <summary>担当変更後も、保持記録にある元の一件と現在の実行実体を返します。</summary>
    /// <returns>予定入力の状態。未受付または記録を確認できない場合はnull。</returns>
    public async Task<WorkflowRunStatus?> ReadAsync(Guid id, CancellationToken cancellationToken = default)
    {
        foreach (bool local in new[] { false, true })
        {
            ScheduledInputRecord? record = (await store.ReadAsync(id, local, cancellationToken).ConfigureAwait(false))?.Record;
            if (record is not null)
            {
                bool running = record.OwnerInstanceId == cluster.LocalInstanceId && engine.Active().Any(item => item.OperationId == id);
                if (record.OwnerInstanceId == cluster.LocalInstanceId && results.TryGetValue(id, out Completion? completed))
                {
                    return new(id, record.OwnerPeerId, record.WorkflowId, record.Revision, completed.Outcome, running,
                        completed.View?.Report, "schedule.resultUnconfirmed", completed.View?.Summary, completed.View?.DetailReason,
                        OwnerInstanceId: record.OwnerInstanceId);
                }
                EOutcome outcome = record.Outcome ?? EOutcome.Unknown;
                string? reason = record.Reason;
                if (record.Outcome is null && record.CancellationRequested && !record.DispatchPermitted)
                {
                    outcome = EOutcome.Cancelled;
                    reason = "schedule.resultUnconfirmed";
                }
                return new(id, record.OwnerPeerId, record.WorkflowId, record.Revision, outcome, running,
                    record.Result?.Report, reason, record.Result?.Summary, record.Result?.DetailReason,
                    OwnerInstanceId: record.OwnerInstanceId);
            }
        }
        return null;
    }

    /// <summary>ローカルで確定した入力の結果を、元の保持先へ返せるよう登録します。</summary>
    private void Reserved(ScheduledInputRecord input, bool local) => owned[input.Id] = local;

    /// <summary>結果の保存を再確認し、管理実体を失った未開始入力だけを引き継ぎます。</summary>
    /// <returns>今回の保持・引継ぎ確認。</returns>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        foreach (Guid id in results.Keys)
        {
            await FlushAsync(id, cancellationToken).ConfigureAwait(false);
        }
        foreach (bool local in new[] { false, true })
        {
            if (!local && !cluster.GetPeers().Any(peer => peer.InstanceId == cluster.LocalInstanceId && PeerStatusRules.CanParticipate(peer.Status)))
            {
                continue;
            }
            try
            {
                (ScheduledInputIndex Index, Persistence.DurableEntry? Entry) state = await store.IndexAsync(local, cancellationToken).ConfigureAwait(false);
                foreach (ScheduledInputIndexEntry entry in state.Index.Entries)
                {
                    if (results.ContainsKey(entry.Id) || owned.ContainsKey(entry.Id) && engine.HasScheduledInput(entry.Id)
                        || OwnerAvailable(entry.OwnerPeerId, entry.OwnerInstanceId) && entry.OwnerInstanceId != cluster.LocalInstanceId)
                    {
                        continue;
                    }
                    if (entry.OwnerInstanceId != cluster.LocalInstanceId && !local && !string.Equals(cluster.CoordinatorId, cluster.LocalPeerId, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    try
                    {
                        await InspectAsync(entry.Id, local, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception error) when (error is IOException or System.Data.Common.DbException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException or TimeZoneNotFoundException)
                    {
                        Volatile.Write(ref lastProblem, "schedule.inputUnconfirmed");
                    }
                }
            }
            catch (Exception error) when (error is IOException or System.Data.Common.DbException or System.Text.Json.JsonException or InvalidOperationException)
            {
                Volatile.Write(ref lastProblem, "schedule.retentionUnconfirmed");
            }
        }
    }

    /// <summary>元の管理実体と配送許可を読み直し、古い索引だけで再開しません。</summary>
    /// <returns>一件について必要だった引継ぎ確認。</returns>
    private async Task InspectAsync(Guid id, bool local, CancellationToken cancellationToken = default)
    {
        (ScheduledInputRecord Record, Persistence.DurableEntry Entry)? stored = await store.ReadAsync(id, local, cancellationToken).ConfigureAwait(false);
        if (stored is null || stored.Value.Record.Outcome is not null)
        {
            return;
        }
        ScheduledInputRecord record = stored.Value.Record;
        if (record.CancellationRequested && !record.DispatchPermitted)
        {
            ScheduledInputRecord? cancelled = await store.ChangeAsync(id, local, current =>
            {
                if (current.CancellationRequested && !current.DispatchPermitted)
                {
                    return current with { Outcome = EOutcome.Cancelled, Reason = "schedule.inputCancelled" };
                }
                return null;
            }, cancellationToken).ConfigureAwait(false);
            if (cancelled is null)
            {
                Volatile.Write(ref lastProblem, "schedule.resultUnconfirmed");
            }
            return;
        }
        if (record.OwnerInstanceId != cluster.LocalInstanceId)
        {
            if (OwnerAvailable(record.OwnerPeerId, record.OwnerInstanceId))
            {
                return;
            }
            if (record.DispatchPermitted)
            {
                ScheduledInputRecord? lost = await store.ChangeAsync(id, local, current =>
                {
                    if (!OwnerAvailable(current.OwnerPeerId, current.OwnerInstanceId) && current.DispatchPermitted && current.Outcome is null)
                    {
                        return current with { Outcome = EOutcome.Unknown, Reason = "schedule.ownerResultUnavailable" };
                    }
                    return null;
                }, cancellationToken).ConfigureAwait(false);
                if (lost?.Outcome == EOutcome.Unknown)
                {
                    Volatile.Write(ref lastProblem, "schedule.ownerResultUnavailable");
                }
                return;
            }
            ScheduledInputRecord? claimed = await store.ChangeAsync(id, local, current =>
            {
                if (!current.DispatchPermitted && current.Outcome is null && !OwnerAvailable(current.OwnerPeerId, current.OwnerInstanceId))
                {
                    return current with { OwnerPeerId = cluster.LocalPeerId, OwnerInstanceId = cluster.LocalInstanceId };
                }
                return null;
            }, cancellationToken).ConfigureAwait(false);
            if (claimed is null)
            {
                return;
            }
            record = claimed;
        }
        owned[id] = local;
        if (record.DispatchPermitted || engine.HasScheduledInput(id) || results.ContainsKey(id) || offered.ContainsKey(id))
        {
            return;
        }
        SourceExecutionInfo? source = sources.GetSources().FirstOrDefault(item => item.Id == record.Source.Id);
        if (source?.Status.State is ESourceState.Starting or ESourceState.Listening or ESourceState.Unavailable)
        {
            return;
        }
        if (source?.Status.State == ESourceState.Cancelled)
        {
            results[id] = new(local, EOutcome.Cancelled, null, "schedule.sourceCancelled");
            return;
        }
        CompiledWorkflow? plan = sources.GetEffective(record.WorkflowId);
        if (!string.Equals(plan?.Revision, record.Revision, StringComparison.Ordinal))
        {
            plan = null;
            if (record.Input is not null)
            {
                plan = compiler.Compile(record.Input.Definition).Workflow;
            }
        }
        if (plan is null || record.Input is null || !string.Equals(plan.Revision, record.Revision, StringComparison.Ordinal))
        {
            results[id] = new(local, EOutcome.NotExecuted, null, "schedule.definitionUnavailable");
            return;
        }
        NodeEmission emission = record.Input.Restore(plan, TimeProvider.System);
        if (offered.TryAdd(id, 0))
        {
            await sources.ResumeScheduledAsync(record.Source.Id, emission, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>配送許可を共有してから実要求を送ります。別の実体が保持する回には許可しません。</summary>
    /// <returns>許可した場合はnull、それ以外は確認不能の結果。</returns>
    private async ValueTask<NodeResult?> PermitAsync(RemoteExecutionRequest request, CancellationToken cancellationToken = default)
    {
        if (permitted.ContainsKey(request.CausationId))
        {
            return null;
        }
        if (!owned.TryGetValue(request.CausationId, out bool local))
        {
            return NodeResult.Failure("schedule.ownerUnconfirmed", "予定入力を保持する元の管理実体を確認できません。", EOutcome.Unknown);
        }
        try
        {
            ScheduledInputRecord? record = await store.ChangeAsync(request.CausationId, local, current =>
            {
                if (current.OwnerInstanceId == cluster.LocalInstanceId && string.Equals(current.OwnerPeerId, cluster.LocalPeerId, StringComparison.Ordinal)
                    && current.Outcome is null && !current.CancellationRequested && string.Equals(current.WorkflowId, request.WorkflowId, StringComparison.Ordinal)
                    && string.Equals(current.Revision, request.Revision, StringComparison.Ordinal))
                {
                    return current with { DispatchPermitted = true };
                }
                return null;
            }, cancellationToken).ConfigureAwait(false);
            if (record?.DispatchPermitted == true)
            {
                permitted[request.CausationId] = 0;
                return null;
            }
        }
        catch (Exception error) when (error is IOException or System.Data.Common.DbException or System.Text.Json.JsonException or InvalidOperationException)
        {
            Volatile.Write(ref lastProblem, "schedule.dispatchUnconfirmed");
        }
        return NodeResult.Failure("schedule.dispatchUnconfirmed", "予定入力の配送開始を確定できないため、処理を開始しません。", EOutcome.Unknown);
    }

    /// <summary>確認できた実行結果を保持し、保存不明でも同じ回を再配送しません。</summary>
    /// <returns>実行結果の保存試行。</returns>
    private async ValueTask CompletedAsync(ExecutionReport report, CancellationToken cancellationToken = default)
    {
        if (owned.TryGetValue(report.Id, out bool local))
        {
            results[report.Id] = new(local, report.Outcome, ExecutionReportPresentation.Bound(report), null);
            await FlushAsync(report.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>実行前の置換・取消・保持拒否を、再開する入力へ戻しません。</summary>
    private void Rejected(NodeEmission emission, NodeResult result)
    {
        if (owned.TryGetValue(emission.Context.CausationId, out bool local))
        {
            results[emission.Context.CausationId] = new(local, result.Outcome, null, result.ReasonCode);
        }
    }

    /// <summary>実行実体が一致する記録にだけ結果を書き込みます。</summary>
    /// <returns>結果保存の試行。</returns>
    private async Task FlushAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (!results.TryGetValue(id, out Completion? result))
        {
            return;
        }
        try
        {
            ScheduledInputRecord? saved = await store.ChangeAsync(id, result.Local, current =>
            {
                if (current.OwnerInstanceId != cluster.LocalInstanceId)
                {
                    return null;
                }
                if (current.Outcome is not (null or EOutcome.Unknown))
                {
                    return current;
                }
                return current with { Outcome = result.Outcome, Result = result.View, Reason = result.Reason };
            }, cancellationToken).ConfigureAwait(false);
            if (saved is not null)
            {
                results.TryRemove(new KeyValuePair<Guid, Completion>(id, result));
                owned.TryRemove(id, out _);
                permitted.TryRemove(id, out _);
                offered.TryRemove(id, out _);
            }
            else
            {
                (ScheduledInputRecord Record, Persistence.DurableEntry Entry)? current = await store.ReadAsync(id, result.Local, cancellationToken).ConfigureAwait(false);
                if (current is not null && current.Value.Record.OwnerInstanceId != cluster.LocalInstanceId)
                {
                    results.TryRemove(new KeyValuePair<Guid, Completion>(id, result));
                    owned.TryRemove(id, out _);
                    permitted.TryRemove(id, out _);
                    offered.TryRemove(id, out _);
                }
            }
        }
        catch (Exception error) when (error is IOException or System.Data.Common.DbException or System.Text.Json.JsonException or InvalidOperationException)
        {
            Volatile.Write(ref lastProblem, "schedule.resultUnconfirmed");
        }
    }

    /// <summary>同名Peerの再起動を、元の実行実体の継続とみなしません。</summary>
    /// <returns>元の実体が通常処理に参加できるか。</returns>
    private bool OwnerAvailable(string peerId, Guid instance) => cluster.GetPeers().Any(peer => string.Equals(peer.PeerId, peerId, StringComparison.Ordinal)
        && peer.InstanceId == instance && (PeerStatusRules.CanParticipate(peer.Status) || peer.InstanceId == cluster.LocalInstanceId && peer.Status == EPeerStatus.Detached));

    /// <summary>Sourceと実行の終了後に、予約通知と保存受付を解放します。</summary>
    public void Dispose()
    {
        store.Reserved -= Reserved;
        store.Dispose();
    }

    /// <summary>保存できるまで保持する、確認済みの一件結果。</summary>
    /// <param name="Local">ローカル保存の入力か。</param>
    /// <param name="Outcome">実行または拒否の結果。</param>
    /// <param name="View">実行できた場合の結果表示。</param>
    /// <param name="Reason">拒否などの理由。</param>
    private sealed record Completion(bool Local, EOutcome Outcome, ExecutionResultView? View, string? Reason);
}
