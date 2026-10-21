using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.Availability;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Availability;

/// <summary>内部データのローカル変更だけを扱い、外部副作用の要求を保管しません。</summary>
internal sealed class AvailabilityOfflineState(string peerId, AvailabilityCache cache, Func<EStoragePurpose, AvailabilityPolicy> policy, Func<AvailabilityEntry, bool> put)
{
    /// <summary>方針に応じて利用不能・確認済み読取り専用・ローカル値を返します。</summary>
    /// <returns>古い可能性と書込可否を明示した値です。</returns>
    internal AvailabilityValue Read(EStoragePurpose purpose, string datasetId)
    {
        AvailabilityEntry? entry = cache.Get(purpose, datasetId);
        EAvailabilityOffline mode = policy(purpose).Offline;
        AvailabilityState state = entry?.State ?? new(purpose, datasetId, peerId, null, null, null);
        string capacity = "Availability.CacheCapacity";
        string transition = "Detached.AvailabilityCandidatePending";
        if (string.Equals(state.Reason, capacity, StringComparison.Ordinal) || string.Equals(state.Reason, transition, StringComparison.Ordinal))
        {
            return new(null, null, state with { IsStale = true, CanWrite = false });
        }
        state = state with { IsStale = true, CanRead = false, CanWrite = false, Reason = "Availability.OfflineUnavailable" };
        if (entry is null || mode == EAvailabilityOffline.Unavailable)
        {
            if (entry is not null)
            {
                _ = put(entry with { State = state });
            }
            return new(null, null, state);
        }
        if (mode == EAvailabilityOffline.LastConfirmedReadOnly)
        {
            state = state with { CanRead = true, Reason = "Availability.LastConfirmedReadOnly" };
            _ = put(entry with { State = state });
            return new(entry.Confirmed?.Copy(), state.SavedRevision, state);
        }
        state = state with { CanRead = true, CanWrite = true, Reason = "Availability.LocalUnconfirmed" };
        if (entry.PersistenceOutcome == EOutcome.Unknown)
        {
            state = state with { CanWrite = false, Reason = "Availability.OriginalWriteUnknown" };
        }
        _ = put(entry with { State = state });
        return new(entry.Effective?.Copy(), state.EffectiveRevision, state);
    }

    /// <summary>確認済みの基準がある内部データだけを端末内で変更します。</summary>
    /// <returns>共有・永続保存の成功とは別のローカル採用です。</returns>
    internal AvailabilityWriteResult Stage(EStoragePurpose purpose, string datasetId, NodeValue value, string? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(value);
        AvailabilityEntry? entry = cache.Get(purpose, datasetId);
        if (policy(purpose).Offline != EAvailabilityOffline.LocalUnconfirmed || entry is null || !entry.State.CanRead || (!entry.State.CanWrite && !entry.State.IsStale) || (entry.State.IsLocalUnconfirmed && entry.State.CandidateRevision is null))
        {
            return new(EOutcome.NotExecuted, false, Read(purpose, datasetId), "Availability.OfflineWriteDenied");
        }
        if (entry.PersistenceOutcome == EOutcome.Unknown)
        {
            return new(EOutcome.Unknown, false, AvailabilityCache.Current(entry), "Availability.OriginalWriteUnknown");
        }
        if (!string.Equals(entry.State.EffectiveRevision, expectedRevision, StringComparison.Ordinal))
        {
            return new(EOutcome.Failed, false, Read(purpose, datasetId), "Availability.LocalRevisionConflict");
        }
        Guid operationId = Guid.NewGuid();
        string revision = "local:" + operationId.ToString("N");
        NodeValue owned = value.Copy();
        AvailabilityEntry next = entry with { Candidate = owned, Effective = owned, Active = true, OperationId = operationId, PersistenceOutcome = EOutcome.NotExecuted, Fallback = null, ReconciliationResolved = false, State = entry.State with { EffectiveRevision = revision, CandidateRevision = revision, IsUnsaved = true, CandidateIsUnsaved = true, IsLocalUnconfirmed = true, IsStale = true, CanWrite = true, Reason = "Availability.LocalUnconfirmed" } };
        if (!put(next))
        {
            return new(EOutcome.NotExecuted, false, AvailabilityCache.Current(entry), "Availability.CacheCapacity");
        }
        return new(EOutcome.NotExecuted, true, AvailabilityCache.Current(next), "Availability.LocalUnconfirmed");
    }

    /// <summary>登録済み内部データの比較保存だけを行い、元の不明書込みは自動再要求しません。</summary>
    /// <returns>非競合統合または保持した競合・不明です。</returns>
    internal async Task<AvailabilityWriteResult> ReconcileAsync(EStoragePurpose purpose, string datasetId, Func<string?, NodeValue, Guid, CancellationToken, Task<AvailabilityWriteResult>> persist, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(persist);
        AvailabilityEntry? entry = cache.Get(purpose, datasetId);
        if (entry?.State.IsLocalUnconfirmed != true || entry.Candidate is null)
        {
            return new(EOutcome.NotExecuted, false, Read(purpose, datasetId), "Availability.NoLocalChange");
        }
        if (entry.PersistenceOutcome == EOutcome.Unknown)
        {
            return new(EOutcome.Unknown, false, AvailabilityCache.Current(entry), "Availability.OriginalWriteUnknown");
        }
        if (entry.ReconciliationResolved)
        {
            return new(entry.PersistenceOutcome, false, AvailabilityCache.Current(entry), entry.State.Reason);
        }
        AvailabilityWriteResult result;
        try
        {
            result = await persist(entry.State.SavedRevision, entry.Candidate.Copy(), entry.OperationId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            result = new(EOutcome.Unknown, false, null, "Availability.OriginalWriteUnknown");
        }
        return ApplyResult(entry, result);
    }

    /// <summary>元のローカル候補IDと一致する照会結果を取り込みます。</summary>
    /// <returns>照会確定だけを反映した状態です。</returns>
    internal AvailabilityWriteResult Resolve(EStoragePurpose purpose, string datasetId, Guid operationId, AvailabilityWriteResult confirmedResult)
    {
        ArgumentNullException.ThrowIfNull(confirmedResult);
        AvailabilityEntry? entry = cache.Get(purpose, datasetId);
        if (operationId == Guid.Empty || entry is null || entry.OperationId != operationId)
        {
            return new(EOutcome.NotExecuted, false, cache.Current(purpose, datasetId), "Availability.OperationMismatch");
        }
        if (entry.ReconciliationResolved)
        {
            return new(entry.PersistenceOutcome, entry.Candidate is null, AvailabilityCache.Current(entry), entry.State.Reason);
        }
        return ApplyResult(entry, confirmedResult);
    }

    /// <summary>書込み応答と読み取り専用照会の確定処理を共用します。</summary>
    /// <returns>再送しない採用または候補保持です。</returns>
    private AvailabilityWriteResult ApplyResult(AvailabilityEntry entry, AvailabilityWriteResult result)
    {
        if (result.PersistenceOutcome == EOutcome.Succeeded && result.Applied && result.Current is not null)
        {
            AvailabilityState state = entry.State with { SavedRevision = result.Current.Revision, EffectiveRevision = result.Current.Revision, RecoveryRevision = result.Current.Revision, CandidateRevision = null, IsUnsaved = false, CandidateIsUnsaved = false, IsLocalUnconfirmed = false, IsStale = false, CanRead = true, CanWrite = true, Reason = null, FallbackBackend = null };
            NodeValue? confirmed = result.Current.Value?.Copy();
            AvailabilityEntry merged = new(state, confirmed, confirmed, OperationId: entry.OperationId, ReconciliationResolved: true);
            if (!put(merged))
            {
                merged = AvailabilityCache.WithoutConfirmedBody(merged, true);
                _ = put(merged);
            }
            return new(EOutcome.Succeeded, true, AvailabilityCache.Current(merged));
        }
        string reason = "Availability.ReconcileConflict";
        bool unknown = result.PersistenceOutcome == EOutcome.Unknown || result.Applied;
        if (unknown)
        {
            reason = "Availability.OriginalWriteUnknown";
        }
        EOutcome outcome = result.PersistenceOutcome;
        if (unknown)
        {
            outcome = EOutcome.Unknown;
        }
        AvailabilityEntry blocked = entry with { PersistenceOutcome = outcome, ReconciliationResolved = !unknown, State = entry.State with { CanWrite = !unknown && policy(entry.State.Purpose).Offline == EAvailabilityOffline.LocalUnconfirmed, Reason = reason } };
        _ = put(blocked);
        return new(outcome, false, AvailabilityCache.Current(blocked), reason);
    }
}
