using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Replication;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Runtime.Scheduling;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Scheduling;

/// <summary>予定位置と入力を同じ取引で保存し、未完了分の保持量を制限します。</summary>
public sealed class ScheduledInputStore : IDisposable
{
    internal static string InputScope => "execution:scheduled-inputs";
    internal static string IndexScope => "execution:scheduled-index";
    internal static string IndexKey => "active";
    private readonly PeerCluster cluster;
    private readonly IDurableRepository repository;
    private readonly ReplicatedEntryConfirmation confirmation;
    private readonly SemaphoreSlim changes = new(1, 1);

    /// <summary>ローカルで保持を確定した入力を、実行管理へ登録します。</summary>
    internal event Action<ScheduledInputRecord, bool>? Reserved;

    /// <summary>既存の保存と共有確認を予定入力にも使用します。</summary>
    public ScheduledInputStore(PeerCluster cluster, IDurableRepository repository)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(repository);
        this.cluster = cluster;
        this.repository = repository;
        confirmation = new(cluster, repository);
    }

    /// <summary>入力、索引、進行位置をまとめて保存します。確定した版競合だけは新しい索引で再検査します。</summary>
    /// <returns>その回の保持を含む原子的な保存結果。</returns>
    internal async ValueTask<DurableReceipt> ReserveAsync(ScheduleInputSnapshot input, bool local,
        Func<Guid, IReadOnlyList<PeerInfo>, string?, DurableMutation> progress, CancellationToken cancellationToken = default)
    {
        long bytes = JsonSerializer.SerializeToUtf8Bytes(input, ContractJson.Options).LongLength;
        await changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                (ScheduledInputIndex Index, DurableEntry? Entry) current = await IndexAsync(local, cancellationToken).ConfigureAwait(false);
                if (512 <= current.Index.Entries.Count || 128L * 1024 * 1024 < current.Index.Entries.Sum(entry => entry.Bytes) + bytes)
                {
                    return await RejectAsync(progress, local, "schedule.retentionCapacity", cancellationToken).ConfigureAwait(false);
                }
                Guid id = Guid.NewGuid();
                PeerInfo[] members = Members(local);
                ScheduledInputRecord record = new(input.Id, input.Definition.Id, input.Revision, input.Source, cluster.LocalPeerId, cluster.LocalInstanceId,
                    false, id, members, input);
                ScheduledInputIndex next = new(id, members, [.. current.Index.Entries, new(input.Id, input.Definition.Id, bytes, cluster.LocalPeerId, cluster.LocalInstanceId, false)]);
                DurableMutation[] mutations = [progress(id, members, null), new(Scope(InputScope, local), input.Id.ToString("N"), null, NodeValue.FromObject(record)),
                    new(Scope(IndexScope, local), IndexKey, current.Entry?.Version, NodeValue.FromObject(next))];
                DurableReceipt saved = await SaveAsync(id, mutations, members, local, cancellationToken).ConfigureAwait(false);
                if (saved.Outcome == EOutcome.Succeeded)
                {
                    Reserved?.Invoke(record, local);
                    return saved;
                }
                string conflict = "Persistence.VersionConflict";
                if (saved.Outcome != EOutcome.Failed || !string.Equals(saved.Reason, conflict, StringComparison.Ordinal))
                {
                    return saved;
                }
            }
            return await RejectAsync(progress, local, "schedule.indexBusy", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            changes.Release();
        }
    }

    /// <summary>保持できない回も予定位置へ記録し、担当交代で同じ回を再発生させません。</summary>
    /// <returns>拒否の記録を確定できた場合は未実行、それ以外は保存の確認結果。</returns>
    private async ValueTask<DurableReceipt> RejectAsync(Func<Guid, IReadOnlyList<PeerInfo>, string?, DurableMutation> progress, bool local, string reason,
        CancellationToken cancellationToken = default)
    {
        Guid id = Guid.NewGuid();
        PeerInfo[] members = Members(local);
        DurableReceipt saved = await SaveAsync(id, [progress(id, members, reason)], members, local, cancellationToken).ConfigureAwait(false);
        if (saved.Outcome == EOutcome.Succeeded)
        {
            return saved with { Outcome = EOutcome.NotExecuted, Reason = reason };
        }
        return saved;
    }

    /// <summary>未完了の索引を、元の確定内容と照合して読みます。</summary>
    /// <returns>確認済み索引と比較更新用の版。</returns>
    internal async ValueTask<(ScheduledInputIndex Index, DurableEntry? Entry)> IndexAsync(bool local, CancellationToken cancellationToken = default)
    {
        string scope = Scope(IndexScope, local);
        if ((await repository.GetConflictsAsync(scope, cancellationToken).ConfigureAwait(false)).Count != 0)
        {
            throw new InvalidOperationException("予定入力の索引に未解消の競合があります。");
        }
        DurableEntry? entry = await repository.ReadAsync(scope, IndexKey, cancellationToken).ConfigureAwait(false);
        if (entry?.Value is null)
        {
            return (new(Guid.Empty, [], []), entry);
        }
        ScheduledInputIndex index = entry.Value.Json.Deserialize<ScheduledInputIndex>(ContractJson.Options) ?? throw new JsonException();
        if (512 < index.Entries.Count || index.Entries.Select(item => item.Id).Distinct().Count() != index.Entries.Count
            || !await confirmation.CheckAsync(entry, index.ChangeId, index.ConfirmedPeers, local, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("予定入力の索引の確定結果を確認できません。");
        }
        return (index, entry);
    }

    /// <summary>一件の内容と、その内容を確定した証拠を読みます。</summary>
    /// <returns>保持した入力の現在の記録。</returns>
    internal async ValueTask<(ScheduledInputRecord Record, DurableEntry Entry)?> ReadAsync(Guid id, bool local, CancellationToken cancellationToken = default)
    {
        string scope = Scope(InputScope, local);
        string key = id.ToString("N");
        if ((await repository.GetConflictsAsync(scope, cancellationToken).ConfigureAwait(false)).Any(entry => string.Equals(entry.Key, key, StringComparison.Ordinal)))
        {
            return null;
        }
        DurableEntry? entry = await repository.ReadAsync(scope, key, cancellationToken).ConfigureAwait(false);
        if (entry?.Value is null)
        {
            return null;
        }
        ScheduledInputRecord record = entry.Value.Json.Deserialize<ScheduledInputRecord>(ContractJson.Options) ?? throw new JsonException();
        if (record.Id != id || !await confirmation.CheckAsync(entry, record.ChangeId, record.ConfirmedPeers, local, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        return (record, entry);
    }

    /// <summary>管理実体・配送許可・完了結果を索引と同時に比較更新します。</summary>
    /// <returns>確定した記録。条件が変わった場合や共有不明の場合はnull。</returns>
    internal async ValueTask<ScheduledInputRecord?> ChangeAsync(Guid inputId, bool local, Func<ScheduledInputRecord, ScheduledInputRecord?> change,
        CancellationToken cancellationToken = default)
    {
        await changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                (ScheduledInputIndex Index, DurableEntry? Entry) index = await IndexAsync(local, cancellationToken).ConfigureAwait(false);
                (ScheduledInputRecord Record, DurableEntry Entry)? current = await ReadAsync(inputId, local, cancellationToken).ConfigureAwait(false);
                if (current is null)
                {
                    return null;
                }
                ScheduledInputRecord? next = change(current.Value.Record);
                if (next is null || next.Id != inputId || current.Value.Record.CancellationRequested && !next.CancellationRequested || current.Value.Record.DispatchPermitted && (!next.DispatchPermitted
                    || !string.Equals(next.OwnerPeerId, current.Value.Record.OwnerPeerId, StringComparison.Ordinal) || next.OwnerInstanceId != current.Value.Record.OwnerInstanceId))
                {
                    return null;
                }
                if (next == current.Value.Record)
                {
                    return next;
                }
                if (await repository.HasPendingWriteAsync(current.Value.Entry.Scope, current.Value.Entry.Key, cancellationToken).ConfigureAwait(false)
                    || await repository.HasPendingWriteAsync(Scope(IndexScope, local), IndexKey, cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }
                ScheduledInputIndexEntry? position = index.Index.Entries.FirstOrDefault(entry => entry.Id == inputId);
                if (position is null && next.Outcome is null)
                {
                    return null;
                }
                Guid operation = Guid.NewGuid();
                PeerInfo[] members = Members(local);
                next = next with { ChangeId = operation, ConfirmedPeers = members };
                List<ScheduledInputIndexEntry> entries = index.Index.Entries.Where(entry => entry.Id != inputId).ToList();
                if (next.Outcome is null)
                {
                    entries.Add(position! with { OwnerPeerId = next.OwnerPeerId, OwnerInstanceId = next.OwnerInstanceId, DispatchPermitted = next.DispatchPermitted });
                }
                else
                {
                    next = next with { Input = null };
                }
                ScheduledInputIndex nextIndex = new(operation, members, entries);
                DurableMutation[] mutations = [new(Scope(InputScope, local), inputId.ToString("N"), current.Value.Entry.Version, NodeValue.FromObject(next)),
                    new(Scope(IndexScope, local), IndexKey, index.Entry?.Version, NodeValue.FromObject(nextIndex))];
                DurableReceipt saved = await SaveAsync(operation, mutations, members, local, cancellationToken).ConfigureAwait(false);
                if (saved.Outcome == EOutcome.Succeeded)
                {
                    return next;
                }
                string conflict = "Persistence.VersionConflict";
                if (saved.Outcome != EOutcome.Failed || !string.Equals(saved.Reason, conflict, StringComparison.Ordinal))
                {
                    return null;
                }
            }
            return null;
        }
        finally
        {
            changes.Release();
        }
    }

    /// <summary>ローカル実行の記録を共有索引へ混ぜません。</summary>
    /// <returns>記録を保持する実保存領域。</returns>
    internal string Scope(string name, bool local)
    {
        if (local)
        {
            return "peer:" + cluster.LocalPeerId + ":" + name;
        }
        return name;
    }

    /// <summary>今回の保存で確認する実体集合を取得します。</summary>
    /// <returns>現在の共有参加者、ローカル保存なら自Peerだけ。</returns>
    private PeerInfo[] Members(bool local) => cluster.GetPeers().Where(peer => PeerStatusRules.CanParticipate(peer.Status) || local && peer.InstanceId == cluster.LocalInstanceId).ToArray();

    /// <summary>同じ取引をローカルまたは固定参加者へ保存します。</summary>
    /// <returns>原子的な保存の確認結果。</returns>
    private ValueTask<DurableReceipt> SaveAsync(Guid id, IReadOnlyList<DurableMutation> mutations, IReadOnlyList<PeerInfo> members, bool local, CancellationToken cancellationToken = default)
    {
        if (local)
        {
            return repository.ApplyBatchAsync(id, mutations, cancellationToken);
        }
        return cluster.ReplicateFixedAsync(id, mutations, members, cancellationToken);
    }

    /// <summary>進行中の操作が終了した後に、ローカルの更新受付を解放します。</summary>
    public void Dispose() => changes.Dispose();
}
