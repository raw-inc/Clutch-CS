using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.Availability;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Availability;

/// <summary>用途の候補採用を永続保存と分け、有限の確認済み値と候補だけを保持します。</summary>
public sealed class AvailabilityService : IDisposable
{
    private readonly string peerId;
    private readonly AvailabilityOptions options;
    private readonly AvailabilityCache cache;
    private readonly AvailabilityFallbackStore fallback;
    private readonly AvailabilityOfflineState offline;
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>明示用途方針と、既存defaultのローカル操作履歴を指定します。</summary>
    public AvailabilityService(string peerId, AvailabilityOptions options, IDurableRepository journalRepository, string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peerId);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(journalRepository);
        if (!Path.IsPathFullyQualified(dataDirectory) || options.MaximumItems is < 1 or > 10000 || options.MaximumBytes is < 1024 or > 67108864 || options.Purposes?.Any(pair => !Enum.IsDefined(pair.Key) || pair.Value is null || !Enum.IsDefined(pair.Value.SaveFailure) || !Enum.IsDefined(pair.Value.Offline) || (pair.Value.FallbackBackend.HasValue && !Enum.IsDefined(pair.Value.FallbackBackend.Value))) == true)
        {
            throw new ArgumentException("保存用途方針または有限上限が不正です。", nameof(options));
        }
        this.peerId = peerId;
        this.options = options with { Purposes = options.Purposes?.ToDictionary(pair => pair.Key, pair => pair.Value) };
        cache = new(this.options);
        fallback = new(journalRepository, dataDirectory, peerId);
        offline = new(peerId, cache, Policy, Put);
    }

    /// <summary>値をそのまま出力せず、保存状態の変更だけを通知します。</summary>
    public Action<AvailabilityState>? StateChanged
    {
        get; set;
    }

    /// <summary>全接続クライアントへ表示できる有限のメタデータ一覧です。</summary>
    /// <returns>秘密値や候補本文を含まない状態です。</returns>
    public IReadOnlyList<AvailabilityState> GetStates() => cache.States();

    /// <summary>primary未作成でも明示代替媒体の採用済み対象を発見します。</summary>
    /// <returns>本文や未採用候補を含めない復旧対象IDです。</returns>
    public Task<IReadOnlyList<string>> ListRecoveryDatasetsAsync(EStoragePurpose purpose, CancellationToken cancellationToken = default) => fallback.ListActiveAsync(Policy(purpose).FallbackBackend, purpose, options.MaximumItems, cancellationToken);

    /// <summary>未指定では旧確認済み維持・オフライン読取り専用です。</summary>
    /// <returns>この用途の事前方針です。</returns>
    public AvailabilityPolicy Policy(EStoragePurpose purpose) => options.Purposes?.GetValueOrDefault(purpose) ?? new();

    /// <summary>内部データのオフライン読取りを用途方針へ限定します。</summary>
    /// <returns>確認済み値または明示した利用不能です。</returns>
    public AvailabilityValue ReadOffline(EStoragePurpose purpose, string datasetId) => offline.Read(purpose, datasetId);

    /// <summary>内部データだけを端末内未確定変更として保持します。</summary>
    /// <returns>共有済みとは異なるローカル変更結果です。</returns>
    public Task<AvailabilityWriteResult> StageOfflineAsync(EStoragePurpose purpose, string datasetId, NodeValue value, string? expectedRevision, CancellationToken cancellationToken = default) => ExclusiveAsync(() => Task.FromResult(offline.Stage(purpose, datasetId, value, expectedRevision)), cancellationToken);

    /// <summary>安全に比較保存できる内部データだけを再接続統合します。</summary>
    /// <returns>競合や元書込み不明を消さない統合結果です。</returns>
    public Task<AvailabilityWriteResult> ReconcileAsync(EStoragePurpose purpose, string datasetId, Func<string?, NodeValue, Guid, CancellationToken, Task<AvailabilityWriteResult>> persist, CancellationToken cancellationToken = default) => ExclusiveAsync(() => offline.ReconcileAsync(purpose, datasetId, persist, cancellationToken), cancellationToken);

    /// <summary>保存成功と実採用成功を分け、スナップショット失敗時の旧volatile有効版を失いません。</summary>
    /// <returns>確認した値と現在の保存状態です。</returns>
    public Task<AvailabilityValue> RememberConfirmedAsync(EStoragePurpose purpose, string datasetId, NodeValue? value, string? revision, bool adopted = false, CancellationToken cancellationToken = default) => ExclusiveAsync(async () =>
    {
        ValidateDataset(purpose, datasetId);
        NodeValue? owned = value?.Copy();
        AvailabilityEntry? previous = cache.Get(purpose, datasetId);
        AvailabilityState state = previous?.State ?? new(purpose, datasetId, peerId, null, null, null);
        state = state with { SavedRevision = revision, IsStale = false };
        AvailabilityEntry next = new(state, owned, previous?.Effective, previous?.Candidate, previous?.OperationId ?? Guid.Empty, previous?.PersistenceOutcome ?? EOutcome.Succeeded, previous?.Active ?? false, previous?.Fallback, previous?.ApplyUnconfirmed ?? false, previous?.ReconciliationResolved ?? false);
        if (adopted)
        {
            if (previous?.State.FallbackBackend is not null)
            {
                await fallback.ClearAsync(purpose, datasetId, cancellationToken).ConfigureAwait(false);
            }
            next = new(state with { EffectiveRevision = revision, RecoveryRevision = revision, IsUnsaved = false, IsLocalUnconfirmed = false, CandidateRevision = null, CandidateIsUnsaved = false, FallbackBackend = null, Reason = null, CanRead = true, CanWrite = true }, owned, owned);
        }
        if (!Put(next))
        {
            next = AvailabilityCache.WithoutConfirmedBody(next, adopted);
            _ = Put(next);
            return new(owned, revision, next.State);
        }
        return AvailabilityCache.Current(next);
    }, cancellationToken);

    /// <summary>現在値を候補本文と混同せず取得します。</summary>
    /// <returns>現在値とメタデータ、未保持ならnullです。</returns>
    public AvailabilityValue? TryGetCurrent(EStoragePurpose purpose, string datasetId) => cache.Current(purpose, datasetId);

    /// <summary>確認済み保存本文だけを未保存現在値と区別して取得します。</summary>
    /// <returns>読取り専用の確認値、未保持ならnullです。</returns>
    public AvailabilityValue? TryGetConfirmed(EStoragePurpose purpose, string datasetId) => cache.Confirmed(purpose, datasetId);

    /// <summary>元要求の読取り専用照会で確定した結果だけを反映します。</summary>
    /// <returns>書込み再送を行わない候補の確定結果です。</returns>
    public Task<AvailabilityWriteResult> ResolveReconciliationAsync(EStoragePurpose purpose, string datasetId, Guid operationId, AvailabilityWriteResult confirmedResult, CancellationToken cancellationToken = default) => ExclusiveAsync(() => Task.FromResult(offline.Resolve(purpose, datasetId, operationId, confirmedResult)), cancellationToken);

    /// <summary>明示離脱の永続ローカル枝を表示し、オフライン統合候補は作成しません。</summary>
    /// <returns>Detached所有のローカル版と全クライアント共通メタデータです。</returns>
    public Task<AvailabilityValue> RememberLocalBranchAsync(EStoragePurpose purpose, string datasetId, NodeValue? value, string? revision, CancellationToken cancellationToken = default) => ExclusiveAsync(() =>
    {
        ValidateDataset(purpose, datasetId);
        AvailabilityEntry? previous = cache.Get(purpose, datasetId);
        if (previous?.State.CandidateRevision is not null)
        {
            AvailabilityState blocked = previous.State with { CanRead = false, CanWrite = false, Reason = "Detached.AvailabilityCandidatePending" };
            _ = Put(previous with { State = blocked });
            return Task.FromResult(new AvailabilityValue(null, null, blocked));
        }
        AvailabilityState state = new(purpose, datasetId, peerId, revision, revision, revision, IsLocalUnconfirmed: true, Reason: "Detached.LocalOnly");
        AvailabilityEntry entry = new(state, value?.Copy(), value?.Copy());
        if (!Put(entry))
        {
            return Task.FromResult(new AvailabilityValue(value?.Copy(), revision, state with { Reason = "Availability.CacheCapacity" }));
        }
        return Task.FromResult(AvailabilityCache.Current(entry));
    }, cancellationToken);

    /// <summary>確認済み失敗だけで候補を扱い、UnknownやCAS不一致を成功へ変換しません。</summary>
    /// <returns>保存の実際の成否と、現在値へ採用したかです。</returns>
    public Task<AvailabilityWriteResult> HandleSaveFailureAsync(EStoragePurpose purpose, string datasetId, NodeValue candidate, string candidateRevision, string? expectedSavedRevision, Guid operationId, bool confirmedNotSaved, Func<NodeValue, CancellationToken, Task<EOutcome>>? apply = null, CancellationToken cancellationToken = default) => ExclusiveAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateRevision);
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("候補の元操作IDが必要です。", nameof(operationId));
        }
        ValidateDataset(purpose, datasetId);
        AvailabilityEntry? previous = cache.Get(purpose, datasetId);
        if (!confirmedNotSaved || previous is null || !string.Equals(previous.State.SavedRevision, expectedSavedRevision, StringComparison.Ordinal))
        {
            return new AvailabilityWriteResult(EOutcome.Unknown, false, TryGetCurrent(purpose, datasetId), "Availability.SaveNotConfirmed");
        }
        if (previous.OperationId == operationId)
        {
            if (!string.Equals(previous.State.CandidateRevision, candidateRevision, StringComparison.Ordinal) || !string.Equals(ContractJson.Hash(previous.Candidate), ContractJson.Hash(candidate), StringComparison.Ordinal))
            {
                return new(EOutcome.Failed, false, AvailabilityCache.Current(previous), "Availability.OperationMismatch");
            }
            if (previous.Active)
            {
                return new(previous.PersistenceOutcome, true, AvailabilityCache.Current(previous), previous.State.Reason, EOutcome.Succeeded);
            }
            if (previous.ApplyUnconfirmed || previous.PersistenceOutcome == EOutcome.Unknown)
            {
                return new(previous.PersistenceOutcome, false, AvailabilityCache.Current(previous), "Availability.CandidateUnconfirmed", EOutcome.Unknown);
            }
        }
        AvailabilityPolicy policy = Policy(purpose);
        if (policy.SaveFailure == EAvailabilitySaveFailure.KeepConfirmed && policy.FallbackBackend is null)
        {
            return new(EOutcome.Failed, false, AvailabilityCache.Current(previous), "Availability.KeepConfirmed");
        }
        AvailabilityEntry next = previous with { Candidate = candidate.Copy(), OperationId = operationId, PersistenceOutcome = EOutcome.Failed, Active = false, Fallback = null, ApplyUnconfirmed = false, State = previous.State with { CandidateRevision = candidateRevision, CandidateIsUnsaved = true, Reason = "Availability.UnsavedCandidate" } };
        if (!Put(next))
        {
            return new(EOutcome.NotExecuted, false, AvailabilityCache.Current(previous), "Availability.CacheCapacity");
        }
        if (policy.FallbackBackend.HasValue)
        {
            try
            {
                AvailabilityFallbackRecord record = new(purpose, datasetId, candidateRevision, candidate.Copy(), expectedSavedRevision);
                AvailabilityFallbackPointer pointer = await fallback.SaveAsync(policy.FallbackBackend.Value, record, cancellationToken).ConfigureAwait(false);
                next = next with { Fallback = pointer, PersistenceOutcome = EOutcome.Succeeded, State = next.State with { CandidateIsUnsaved = false, FallbackBackend = pointer.Backend, Reason = "Availability.FallbackCandidateSaved" } };
                _ = Put(next);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Data.Common.DbException)
            {
                next = next with { PersistenceOutcome = EOutcome.Unknown, State = next.State with { Reason = "Availability.FallbackUnconfirmed" } };
                _ = Put(next);
                return new(EOutcome.Unknown, false, AvailabilityCache.Current(next), "Availability.FallbackUnconfirmed");
            }
        }
        if (apply is null)
        {
            return new(next.PersistenceOutcome, false, AvailabilityCache.Current(next), "Availability.ActivationRequired");
        }
        return await ApplyAsync(next, apply, cancellationToken).ConfigureAwait(false);
    }, cancellationToken);

    /// <summary>未保存候補の明示確認画面へ本文と状態を渡します。</summary>
    /// <returns>保持候補、なければnullです。</returns>
    public AvailabilityValue? TryGetCandidate(EStoragePurpose purpose, string datasetId)
    {
        AvailabilityEntry? entry = cache.Get(purpose, datasetId);
        if (entry?.State.CandidateRevision is null)
        {
            return null;
        }
        return new(entry.Candidate?.Copy(), entry.State.CandidateRevision, entry.State);
    }

    /// <summary>ドメイン側の版・保護条件・影響確認を経た候補だけを明示採用します。</summary>
    /// <returns>保存と採用を分離した結果です。</returns>
    public Task<AvailabilityWriteResult> ActivateCandidateAsync(EStoragePurpose purpose, string datasetId, Func<NodeValue, CancellationToken, Task<EOutcome>> apply, CancellationToken cancellationToken = default) => ExclusiveAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(apply);
        AvailabilityEntry? entry = cache.Get(purpose, datasetId);
        if (entry?.Candidate is null)
        {
            return new AvailabilityWriteResult(EOutcome.NotExecuted, false, TryGetCurrent(purpose, datasetId), "Availability.CandidateMissing");
        }
        if (entry.PersistenceOutcome == EOutcome.Unknown || entry.ApplyUnconfirmed)
        {
            return new(entry.PersistenceOutcome, false, AvailabilityCache.Current(entry), "Availability.CandidateUnconfirmed", EOutcome.Unknown);
        }
        if (entry.Active)
        {
            return new(entry.PersistenceOutcome, true, AvailabilityCache.Current(entry), entry.State.Reason, EOutcome.Succeeded);
        }
        return await ApplyAsync(entry, apply, cancellationToken).ConfigureAwait(false);
    }, cancellationToken);

    /// <summary>ドメインで確認済み版へ戻した後に未保存候補を解除します。</summary>
    /// <returns>メタデータの解除完了です。</returns>
    public async Task ClearCandidateAsync(EStoragePurpose purpose, string datasetId, CancellationToken cancellationToken = default)
    {
        AvailabilityEntry? entry = cache.Get(purpose, datasetId);
        if (entry is not null)
        {
            _ = await RememberConfirmedAsync(purpose, datasetId, entry.Confirmed, entry.State.SavedRevision, true, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>確認済みactiveの代替候補だけを復旧時の検証へ渡します。</summary>
    /// <returns>ドメイン側が再検証して採用する候補です。</returns>
    public Task<AvailabilityValue?> RestoreCandidateAsync(EStoragePurpose purpose, string datasetId, CancellationToken cancellationToken = default) => ExclusiveAsync(async () =>
    {
        EStorageBackend? backend = Policy(purpose).FallbackBackend;
        AvailabilityEntry? previous = cache.Get(purpose, datasetId);
        if (backend is null || previous is null)
        {
            return null;
        }
        AvailabilityFallbackRecord? record = await fallback.ReadActiveAsync(backend.Value, purpose, datasetId, cancellationToken).ConfigureAwait(false);
        if (record is null || !string.Equals(record.BaseRevision, previous.State.SavedRevision, StringComparison.Ordinal))
        {
            return null;
        }
        AvailabilityEntry next = previous with { Candidate = record.Value.Copy(), PersistenceOutcome = EOutcome.Succeeded, State = previous.State with { CandidateRevision = record.Revision, CandidateIsUnsaved = false, RecoveryRevision = record.Revision, FallbackBackend = backend, Reason = "Availability.FallbackRecovery" } };
        if (!Put(next))
        {
            throw new InvalidDataException("復旧候補が現在の有限保持上限を超えます。");
        }
        return TryGetCandidate(purpose, datasetId);
    }, cancellationToken);

    /// <summary>候補の有効化を実行してから、現在値の状態だけを変更します。</summary>
    /// <returns>元の保存失敗を消さない採用結果です。</returns>
    private async Task<AvailabilityWriteResult> ApplyAsync(AvailabilityEntry entry, Func<NodeValue, CancellationToken, Task<EOutcome>> apply, CancellationToken cancellationToken = default)
    {
        EOutcome outcome;
        try
        {
            outcome = await apply(entry.Candidate!.Copy(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            outcome = EOutcome.Unknown;
        }
        if (outcome != EOutcome.Succeeded)
        {
            if (outcome == EOutcome.Unknown)
            {
                entry = entry with { ApplyUnconfirmed = true, State = entry.State with { Reason = "Availability.ApplyUnconfirmed" } };
                _ = Put(entry);
            }
            return new(entry.PersistenceOutcome, false, AvailabilityCache.Current(entry), "Availability.ApplyRejected", outcome);
        }
        AvailabilityState state = entry.State with { EffectiveRevision = entry.State.CandidateRevision, IsUnsaved = entry.State.CandidateIsUnsaved, Reason = "Availability.UnsavedEffective" };
        if (!state.IsUnsaved && state.FallbackBackend is not null)
        {
            state = state with { Reason = "Availability.FallbackEffective" };
        }
        if (entry.Fallback is not null)
        {
            try
            {
                await fallback.ActivateAsync(state.Purpose, state.DatasetId, entry.Fallback, cancellationToken).ConfigureAwait(false);
                state = state with { RecoveryRevision = state.CandidateRevision, Reason = "Availability.FallbackEffective" };
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Data.Common.DbException)
            {
                state = state with { IsUnsaved = true, Reason = "Availability.ActivationSelectionUnconfirmed" };
            }
        }
        AvailabilityEntry active = entry with { State = state, Effective = entry.Candidate, Active = true };
        _ = Put(active);
        return new(entry.PersistenceOutcome, true, AvailabilityCache.Current(active), state.Reason, EOutcome.Succeeded);
    }

    /// <summary>用途候補の寿命終了後に排他資源を解放します。</summary>
    public void Dispose() => gate.Dispose();

    /// <summary>有限キャッシュ更新と値なし通知を一つの境界へまとめます。</summary>
    /// <returns>容量内で保持できたかです。</returns>
    private bool Put(AvailabilityEntry entry)
    {
        if (!cache.Put(entry))
        {
            return false;
        }
        try
        {
            StateChanged?.Invoke(entry.State);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // 通知先の障害で確認済み採用を取消済みへ戻しません。状態はread APIで取得できます。
        }
        return true;
    }

    /// <summary>同じ候補の保存・採用・復旧を直列化します。</summary>
    /// <returns>候補操作の結果です。</returns>
    private async Task<T> ExclusiveAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>メタデータの識別子自体も有限にします。</summary>
    private static void ValidateDataset(EStoragePurpose purpose, string datasetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetId);
        if (!Enum.IsDefined(purpose) || 1024 < datasetId.Length)
        {
            throw new ArgumentException("保存用途の対象が不正です。", nameof(datasetId));
        }
    }
}
