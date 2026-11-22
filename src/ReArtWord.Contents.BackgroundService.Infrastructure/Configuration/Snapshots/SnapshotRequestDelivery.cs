using System.Text.Json;
using System.Collections.Concurrent;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;

/// <summary>Snapshotの元要求と初回に換算した受付期限を保存し、別Coreからの回復でも同じ期限を使います。</summary>
internal sealed class SnapshotRequestDelivery(IDurableRepository repository,
    Func<ClusterRequest, CancellationToken, Task<ClockedClusterRequest?>> prepare)
{
    private readonly ConcurrentDictionary<(string Scope, Guid Id), RequestAdmission> pending = [];

    /// <summary>保存済みの本文と期限を復元します。旧版の要求には、新しい期限を付与しません。</summary>
    /// <returns>初回の配送情報を持つ元要求、保存前ならnullです。</returns>
    internal async Task<ClusterRequest?> ReadAsync(string scope, Guid id, CancellationToken cancellationToken = default)
    {
        SavedRequest? saved = await LoadAsync(scope, id, cancellationToken).ConfigureAwait(false);
        return saved is null ? null
                             : Ready(saved);
    }

    /// <summary>初回に保存した本文の形式を照合します。配送の可否はPrepareAsyncで別途確認します。</summary>
    /// <returns>受付時計の準備中も含む、変更しない元要求です。</returns>
    internal async Task<ClusterRequest?> ReadOriginalAsync(string scope, Guid id, CancellationToken cancellationToken = default)
        => (await LoadAsync(scope, id, cancellationToken).ConfigureAwait(false))?.Request;

    /// <summary>元要求と補助記録を照合し、準備中と準備済みを区別します。</summary>
    /// <returns>保存された要求と期限の版です。</returns>
    private async Task<SavedRequest?> LoadAsync(string scope, Guid id, CancellationToken cancellationToken = default)
    {
        string key = id.ToString("N");
        DurableEntry? entry = await repository.ReadAsync(scope, key, cancellationToken).ConfigureAwait(false);
        if (entry?.Value is null)
        {
            return null;
        }
        ClusterRequest request = entry.Value.Json.Deserialize<ClusterRequest>(ContractJson.Options)
            ?? throw new InvalidDataException("保存したSnapshot要求を読み取れません。");
        if (request.Id != id)
        {
            throw new InvalidDataException("Snapshot要求の識別子が保存キーと一致しません。");
        }
        DurableEntry? metadata = await repository.ReadAsync(scope, ClockKey(id), cancellationToken).ConfigureAwait(false);
        if (metadata is null)
        {
            // 旧版が保存した本文には、最初に使った時計の情報がありません。別PCの現在時刻から
            // 期限を推測し直すと受付期間が延びるため、元の日時を保った従来形式で送ります。
            return new(request, null, null);
        }
        DeliveryClock saved = metadata.Value?.Json.Deserialize<DeliveryClock>(ContractJson.Options)
            ?? throw new InvalidDataException("Snapshot要求の受付期限を読み取れません。");
        if (saved.Version != 1 || !string.Equals(saved.Fingerprint, ContractJson.Hash(request), StringComparison.Ordinal)
            || saved.Timing is { } stamp && (!saved.Prepared || stamp.InstanceId != request.TargetInstanceId))
        {
            throw new InvalidDataException("Snapshot要求と受付期限の記録が一致しません。");
        }
        return new(request, metadata.Version, saved);
    }

    /// <summary>元要求と準備中の記録を一括保存し、初回の残り時間で時計照会を行います。</summary>
    /// <returns>同時受付でも保存に勝った元要求です。保存結果を確認できなければnullです。</returns>
    internal async Task<ClusterRequest?> PrepareAsync(string scope, ClusterRequest request, CancellationToken cancellationToken = default)
    {
        foreach ((string Scope, Guid Id) expired in pending.Where(pair => pair.Value.IsExpired).Select(pair => pair.Key))
        {
            pending.TryRemove(expired, out _);
        }
        SavedRequest? saved = await LoadAsync(scope, request.Id, cancellationToken).ConfigureAwait(false);
        if (saved is null)
        {
            RequestAdmission admission = request.Admission ?? throw new InvalidDataException("新規Snapshot要求の受付期間がありません。");
            DeliveryClock starting = new(1, ContractJson.Hash(request), false, null);

            // 本文は旧版も読める形のまま残し、準備中の記録と原子的に保存します。
            // 時計照会の途中で終了しても、初回の要求を旧形式の記録と取り違えません。
            // 補助記録は元要求と同じ保存先に置き、単独では回収しません。
            Persistence.Receipts.DurableReceipt receipt = await repository.ApplyBatchAsync(WorkflowStore.PhaseId(request.Id, "request"),
                [new DurableMutation(scope, request.Id.ToString("N"), null, NodeValue.FromObject(request)),
                 new DurableMutation(scope, ClockKey(request.Id), null, NodeValue.FromObject(starting))], cancellationToken).ConfigureAwait(false);
            if (receipt.Outcome == EOutcome.Succeeded)
            {
                pending.TryAdd((scope, request.Id), admission);
            }
            saved = await LoadAsync(scope, request.Id, cancellationToken).ConfigureAwait(false);
        }
        if (saved is null)
        {
            return null;
        }
        if (saved.Clock is not { Prepared: false })
        {
            pending.TryRemove((scope, request.Id), out _);
            return Ready(saved);
        }
        if (!pending.TryGetValue((scope, request.Id), out RequestAdmission? budget) || budget.IsExpired)
        {
            // Coreの再起動後は、この準備を開始した時点からの経過時間を復元できません。
            // 期限を付け直す代わりに送信を止め、元操作の確認不能を呼出元へ返します。
            return await ReadAsync(scope, request.Id, cancellationToken).ConfigureAwait(false);
        }
        ClockedClusterRequest? prepared = await prepare(saved.Request with { Admission = budget }, cancellationToken).ConfigureAwait(false);
        RequestClockStamp? stamp = null;
        if (prepared is not null)
        {
            stamp = new(prepared.ClockInstanceId, prepared.StartByTicks, prepared.SourceNow);
        }
        DeliveryClock metadata = saved.Clock with { Prepared = true, Timing = stamp };
        _ = await repository.ApplyBatchAsync(WorkflowStore.PhaseId(request.Id, "request-clock"),
            [new DurableMutation(scope, ClockKey(request.Id), saved.ClockVersion, NodeValue.FromObject(metadata))], cancellationToken).ConfigureAwait(false);
        ClusterRequest? result = await ReadAsync(scope, request.Id, cancellationToken).ConfigureAwait(false);
        pending.TryRemove((scope, request.Id), out _);
        return result;
    }

    /// <summary>既存要求のキーと衝突しない、同じ保存先の補助キーを作ります。</summary>
    /// <returns>元の操作IDに固定したキーです。</returns>
    private static string ClockKey(Guid id) => id.ToString("N") + ".admission";

    /// <summary>準備途中の要求を、期限情報がない旧形式として送りません。</summary>
    /// <returns>初回の準備を確認できた配送だけを返します。</returns>
    private static ClusterRequest Ready(SavedRequest saved)
    {
        if (saved.Clock is { Prepared: false })
        {
            throw new InvalidDataException("Snapshot要求の初回受付期限を確認できないため、再送できません。");
        }
        return saved.Request with { DeliveryPrepared = true, DeliveryTiming = saved.Clock?.Timing };
    }

    /// <summary>本文の一致を確認できる場合だけ、初回に換算した期限を復元します。</summary>
    private sealed record DeliveryClock(int Version, string Fingerprint, bool Prepared, RequestClockStamp? Timing);

    /// <summary>本文と補助記録を同じ要求として照合した結果です。</summary>
    private sealed record SavedRequest(ClusterRequest Request, string? ClockVersion, DeliveryClock? Clock);
}
