using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Replication;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Runtime.Scheduling;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Scheduling;

/// <summary>時間指定の基準時刻と受付済み回を共有し、担当変更時の重複発生を防ぎます。</summary>
public sealed class ScheduleCursorStore : ISourceScheduleStore
{
    /// <summary>ワークフローごとの時間指定記録を持つ保存領域。</summary>
    public static string ScopePrefix => "execution:schedules:";
    private readonly PeerCluster cluster;
    private readonly IDurableRepository repository;
    private readonly ReplicatedEntryConfirmation confirmation;

    /// <summary>既存の比較更新と、固定した参加者への共有確認を使用します。</summary>
    public ScheduleCursorStore(PeerCluster cluster, IDurableRepository repository)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(repository);
        this.cluster = cluster;
        this.repository = repository;
        confirmation = new(cluster, repository);
        Inputs = new(cluster, repository);
    }

    /// <summary>進行位置と同じ取引で保存する、予定入力の保持先。</summary>
    public ScheduledInputStore Inputs
    {
        get;
    }

    /// <summary>Sourceの新規受付が終了した後、保持済み予定に取消を残します。</summary>
    /// <returns>元のSource実体が受け付けた予定の取消記録を確認できたか。</returns>
    public ValueTask<bool> CancelAsync(RemoteExecutionRequest source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        string timer = "scheduling.timer";
        if (!string.Equals(source.Node.Type, timer, StringComparison.Ordinal))
        {
            return new(true);
        }
        return new ScheduledInputCancellation(cluster, Inputs).CancelSourceAsync(source, cancellationToken);
    }

    /// <summary>一回も発生しない時間指定の終了を記録し、次の担当で終了通知を繰り返しません。</summary>
    /// <returns>比較更新した終了位置。</returns>
    public ValueTask<ScheduleCursor> FinishAsync(RemoteExecutionRequest source, ScheduleCursor cursor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(cursor);
        PeerInfo? local = Local(source);
        if (local is null || cursor.Version is null || cursor.Outcome != EOutcome.Succeeded)
        {
            return new(cursor with { Outcome = EOutcome.NotExecuted, Reason = "schedule.cursorChanged" });
        }
        return SaveAsync(source, cursor with { Exhausted = true }, local, cancellationToken);
    }

    /// <summary>同じ時間指定の記録があれば再利用し、未作成の場合だけ基準時刻を共有します。</summary>
    /// <returns>確認済みの進行位置、または開始できない理由。</returns>
    public async ValueTask<ScheduleCursor> ReadAsync(RemoteExecutionRequest source, string timelineKey, DateTimeOffset initialAnchor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        PeerInfo? local = Local(source);
        string partition = "one";
        if (source.Node.Target?.Scope == EExecutionScope.All)
        {
            partition = "peer:" + source.TargetPeerId;
        }
        else
        {
            if (source.Node.PcExecution == EPcExecutionMode.All)
            {
                partition = "host:" + local?.HostId;
            }
        }
        string key = source.Node.Id + ":" + ContractJson.Hash(new { timelineKey, partition });
        ScheduleCursor initial = new(key, null, initialAnchor);
        if (local is null)
        {
            return initial with { Outcome = EOutcome.NotExecuted, Reason = "schedule.ownerUnavailable" };
        }
        string scope = Scope(source, local);
        if ((await repository.GetConflictsAsync(scope, cancellationToken).ConfigureAwait(false)).Any(entry => string.Equals(entry.Key, key, StringComparison.Ordinal)))
        {
            return initial with { Outcome = EOutcome.Unknown, Reason = "schedule.cursorConflict" };
        }
        DurableEntry? entry = await repository.ReadAsync(scope, key, cancellationToken).ConfigureAwait(false);
        if (entry?.Value is not null)
        {
            ScheduleProgressRecord? saved = entry.Value.Json.Deserialize<ScheduleProgressRecord>(ContractJson.Options);
            if (saved is null || !string.Equals(saved.NodeId, source.Node.Id, StringComparison.Ordinal) || !string.Equals(saved.Cursor.Key, key, StringComparison.Ordinal)
                || saved.Cursor.Count < 0 || saved.Cursor.Outcome != EOutcome.Succeeded
                || !await confirmation.CheckAsync(entry, saved.OperationId, saved.ConfirmedPeers, local.Status == EPeerStatus.Detached, cancellationToken).ConfigureAwait(false))
            {
                return initial with { Outcome = EOutcome.Unknown, Reason = "schedule.cursorUnconfirmed" };
            }
            return saved.Cursor with { Version = entry.Version };
        }
        return await SaveAsync(source, initial with { Version = entry?.Version }, local, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>確認した版からだけ予定位置を進めます。競合した操作で入力を再発生させません。</summary>
    /// <returns>更新後の進行位置と保存結果。</returns>
    public async ValueTask<ScheduleCursor> AdvanceAsync(RemoteExecutionRequest source, ScheduleCursor cursor, ScheduleInputSnapshot input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(input);
        DateTimeOffset scheduledAt = input.Schedule.ScheduledAt;
        PeerInfo? local = Local(source);
        if (local is null || cursor.Outcome != EOutcome.Succeeded || cursor.Version is null || cursor.Count == long.MaxValue
            || cursor.LastScheduledAt is not null && scheduledAt <= cursor.LastScheduledAt || input.Source.Id != source.Id
            || !string.Equals(input.Definition.Id, source.WorkflowId, StringComparison.Ordinal) || !string.Equals(input.Source.NodeId, source.Node.Id, StringComparison.Ordinal))
        {
            return cursor with { Outcome = EOutcome.NotExecuted, Reason = "schedule.cursorChanged" };
        }
        ScheduleCursor next = cursor with { LastScheduledAt = scheduledAt, Count = cursor.Count + 1 };
        string scope = Scope(source, local);
        DurableReceipt saved = await Inputs.ReserveAsync(input, local.Status == EPeerStatus.Detached, (id, members, reason) =>
            new DurableMutation(scope, cursor.Key, cursor.Version, NodeValue.FromObject(new ScheduleProgressRecord(source.Node.Id, next with { Version = null, Reason = reason }, id, members))), cancellationToken).ConfigureAwait(false);
        if (saved.Outcome != EOutcome.Succeeded)
        {
            return cursor with { Outcome = saved.Outcome, Reason = saved.Reason ?? "schedule.cursorUnconfirmed" };
        }
        return next with { Version = saved.Entries.Single(entry => string.Equals(entry.Scope, scope, StringComparison.Ordinal) && string.Equals(entry.Key, cursor.Key, StringComparison.Ordinal)).Version };
    }

    /// <summary>この待受を受け付けた元Peer実体が、今もローカル実行可能かを確認します。</summary>
    /// <returns>元のローカルPeer、または実行不能ならnull。</returns>
    private PeerInfo? Local(RemoteExecutionRequest source)
        => cluster.GetPeers().FirstOrDefault(peer => peer.InstanceId == cluster.LocalInstanceId && peer.InstanceId == source.TargetInstanceId
            && string.Equals(peer.PeerId, source.TargetPeerId, StringComparison.Ordinal) && (PeerStatusRules.CanParticipate(peer.Status) || peer.Status == EPeerStatus.Detached));

    /// <summary>明示離脱中の時間指定記録は、そのPeerのローカル範囲へ保存します。</summary>
    /// <returns>今回読み書きする保存領域。</returns>
    private static string Scope(RemoteExecutionRequest source, PeerInfo local)
    {
        string scope = ScopePrefix + source.WorkflowId;
        if (local.Status == EPeerStatus.Detached)
        {
            scope = "peer:" + local.PeerId + ":" + scope;
        }
        return scope;
    }

    /// <summary>今回の受付を固定した参加者へ共有し、確認できた場合だけ新しい版を返します。</summary>
    /// <returns>比較更新の確認結果。</returns>
    private async ValueTask<ScheduleCursor> SaveAsync(RemoteExecutionRequest source, ScheduleCursor cursor, PeerInfo local, CancellationToken cancellationToken = default)
    {
        PeerInfo[] members = cluster.GetPeers().Where(peer => PeerStatusRules.CanParticipate(peer.Status)).ToArray();
        ScheduleProgressRecord record = new(source.Node.Id, cursor with { Version = null }, Guid.NewGuid(), members);
        DurableMutation mutation = new(Scope(source, local), cursor.Key, cursor.Version, NodeValue.FromObject(record));
        DurableReceipt receipt;
        if (local.Status == EPeerStatus.Detached)
        {
            receipt = await repository.ApplyBatchAsync(record.OperationId, [mutation], cancellationToken).ConfigureAwait(false);
        }
        else
        {
            receipt = await cluster.ReplicateFixedAsync(record.OperationId, [mutation], members, cancellationToken).ConfigureAwait(false);
        }
        if (receipt.Outcome != EOutcome.Succeeded)
        {
            return cursor with { Outcome = receipt.Outcome, Reason = receipt.Reason ?? "schedule.cursorUnconfirmed" };
        }
        return cursor with { Version = receipt.Entries[0].Version };
    }
}
