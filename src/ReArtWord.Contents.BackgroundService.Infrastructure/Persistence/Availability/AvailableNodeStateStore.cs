using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.Availability;
using ReArtWord.Contents.BackgroundService.Contracts.State;
using ReArtWord.Contents.BackgroundService.Runtime.State;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Availability;

/// <summary>弱いget/setを明示し、dedupeとCASの強い保存経路をそのまま残します。</summary>
public sealed class AvailableNodeStateStore(INodeStateStore durable, AvailabilityService availability, Func<bool>? isOnline = null) : IAvailableNodeStateStore, IObservableNodeStateStore
{
    /// <summary>未保存候補とは区別して、実際に保存された値を継続観測します。</summary>
    /// <returns>強い保存先の初期値と変更通知です。</returns>
    public IAsyncEnumerable<StateChange> WatchAsync(string scope, string key, CancellationToken cancellationToken = default)
        => (durable as IObservableNodeStateStore ?? throw new NotSupportedException("状態の変更通知を利用できません。")).WatchAsync(scope, key, cancellationToken);

    /// <summary>dedupeとCAS用の読取りは揮発候補を見ません。</summary>
    /// <returns>既存の確認済み状態です。</returns>
    public ValueTask<VersionedState?> ReadAsync(string scope, string key, CancellationToken cancellationToken = default) => durable.ReadAsync(scope, key, cancellationToken);

    /// <summary>強い比較保存の結果を揮発採用で書き換えません。</summary>
    /// <returns>既存CASの実保存結果です。</returns>
    public ValueTask<StateWriteResult> CompareExchangeAsync(string scope, string key, string? expectedVersion, NodeValue? value, CancellationToken cancellationToken = default) => durable.CompareExchangeAsync(scope, key, expectedVersion, value, cancellationToken);

    /// <summary>通常getは明示未保存値またはオフラインの確認済み値を扱います。</summary>
    /// <returns>状態の区別を含む値です。</returns>
    public async ValueTask<AvailabilityValue> ReadAvailableAsync(string scope, string key, CancellationToken cancellationToken = default)
    {
        string id = DatasetId(scope, key);
        if (isOnline?.Invoke() == false)
        {
            return availability.ReadOffline(EStoragePurpose.Runtime, id);
        }
        try
        {
            VersionedState? current = await durable.ReadAsync(scope, key, cancellationToken).ConfigureAwait(false);
            if (current?.LocalOnly == true)
            {
                return await availability.RememberLocalBranchAsync(EStoragePurpose.Runtime, id, current.Value, current.Version, cancellationToken).ConfigureAwait(false);
            }
            if (availability.TryGetCandidate(EStoragePurpose.Runtime, id) is not null)
            {
                return availability.TryGetCurrent(EStoragePurpose.Runtime, id)!;
            }
            AvailabilityValue result = await availability.RememberConfirmedAsync(EStoragePurpose.Runtime, id, current?.Value, current?.Version, true, cancellationToken).ConfigureAwait(false);
            if (await availability.RestoreCandidateAsync(EStoragePurpose.Runtime, id, cancellationToken).ConfigureAwait(false) is not null)
            {
                AvailabilityWriteResult recovered = await availability.ActivateCandidateAsync(EStoragePurpose.Runtime, id, static (_, _) => Task.FromResult(EOutcome.Succeeded), cancellationToken).ConfigureAwait(false);
                result = recovered.Current ?? result;
            }
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Data.Common.DbException)
        {
            return availability.ReadOffline(EStoragePurpose.Runtime, id);
        }
    }

    /// <summary>保存失敗時も旧値の再読一致を確認するまで候補を使いません。</summary>
    /// <returns>元の保存成否と採用結果です。</returns>
    public async ValueTask<AvailabilityWriteResult> SetAvailableAsync(string scope, string key, NodeValue value, string? expectedRevision, Guid operationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        string id = DatasetId(scope, key);
        if (isOnline?.Invoke() == false)
        {
            return await availability.StageOfflineAsync(EStoragePurpose.Runtime, id, value, expectedRevision, cancellationToken).ConfigureAwait(false);
        }
        AvailabilityValue before = await ReadAvailableAsync(scope, key, cancellationToken).ConfigureAwait(false);
        if (!before.State.CanWrite)
        {
            return new(EOutcome.NotExecuted, false, before, before.State.Reason);
        }
        if (!string.Equals(before.Revision, expectedRevision, StringComparison.Ordinal))
        {
            return new(EOutcome.Failed, false, before, "Availability.LocalRevisionConflict");
        }
        string unknown = "Availability.OriginalWriteUnknown";
        if (string.Equals(before.State.Reason, unknown, StringComparison.Ordinal))
        {
            return new(EOutcome.Unknown, false, before, unknown);
        }
        try
        {
            StateWriteResult written = await durable.CompareExchangeAsync(scope, key, before.State.SavedRevision, value, cancellationToken).ConfigureAwait(false);
            if (written.Outcome != EOutcome.Succeeded || !written.Applied)
            {
                return new(written.Outcome, false, before, written.Reason);
            }
            if (written.LocalOnly)
            {
                AvailabilityValue local = await availability.RememberLocalBranchAsync(EStoragePurpose.Runtime, id, written.Current?.Value, written.Current?.Version, cancellationToken).ConfigureAwait(false);
                return new(written.Outcome, true, local, written.Reason);
            }
            AvailabilityValue current = await availability.RememberConfirmedAsync(EStoragePurpose.Runtime, id, written.Current?.Value, written.Current?.Version, true, cancellationToken).ConfigureAwait(false);
            return new(written.Outcome, true, current, written.Reason);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Data.Common.DbException)
        {
            bool notSaved = false;
            try
            {
                VersionedState? actual = await durable.ReadAsync(scope, key, cancellationToken).ConfigureAwait(false);
                notSaved = string.Equals(actual?.Version, before.State.SavedRevision, StringComparison.Ordinal);
            }
            catch (Exception readException) when (readException is IOException or UnauthorizedAccessException or System.Data.Common.DbException)
            {
                // 旧版を再読できなければ、保存失敗を確定した扱いにしません。
            }
            return await availability.HandleSaveFailureAsync(EStoragePurpose.Runtime, id, value, "unsaved:" + operationId.ToString("N"), before.State.SavedRevision, operationId, notSaved, static (_, _) => Task.FromResult(EOutcome.Succeeded), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>再接続時の非競合内部状態だけを比較保存します。</summary>
    /// <returns>競合・不明を保持する統合結果です。</returns>
    public Task<AvailabilityWriteResult> ReconcileAvailableAsync(string scope, string key, CancellationToken cancellationToken = default)
    {
        string id = DatasetId(scope, key);
        return availability.ReconcileAsync(EStoragePurpose.Runtime, id, async (expected, value, _, token) =>
        {
            StateWriteResult written = await durable.CompareExchangeAsync(scope, key, expected, value, token).ConfigureAwait(false);
            AvailabilityState state = availability.TryGetCurrent(EStoragePurpose.Runtime, id)!.State;
            return new(written.Outcome, written.Applied, new(written.Current?.Value, written.Current?.Version, state), written.Reason);
        }, cancellationToken);
    }

    /// <summary>復旧時に外部actionではなく、未確定内部状態だけを統合します。</summary>
    /// <returns>今回確認できた非競合の統合完了です。</returns>
    public async Task ReconcilePendingAsync(CancellationToken cancellationToken = default)
    {
        if (isOnline?.Invoke() == false)
        {
            return;
        }
        string prefix = "state:";
        string conflict = "Availability.ReconcileConflict";
        string unknown = "Availability.OriginalWriteUnknown";
        foreach (AvailabilityState state in availability.GetStates().Where(state => state.Purpose == EStoragePurpose.Runtime && state.IsLocalUnconfirmed && state.CanRead && state.CandidateRevision is not null && state.DatasetId.StartsWith(prefix, StringComparison.Ordinal) && !string.Equals(state.Reason, conflict, StringComparison.Ordinal) && !string.Equals(state.Reason, unknown, StringComparison.Ordinal)))
        {
            string[] keys = JsonSerializer.Deserialize<string[]>(state.DatasetId[prefix.Length..]) ?? throw new InvalidDataException("内部stateの対象識別子が不正です。");
            _ = await ReconcileAvailableAsync(keys[0], keys[1], cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>ローカル候補を明示破棄し、現在の確認済み値を採用します。</summary>
    /// <returns>外部書込みを行わない現在値です。</returns>
    public async Task<AvailabilityValue> DiscardAvailableAsync(string scope, string key, CancellationToken cancellationToken = default)
    {
        VersionedState? current = await durable.ReadAsync(scope, key, cancellationToken).ConfigureAwait(false);
        return await availability.RememberConfirmedAsync(EStoragePurpose.Runtime, DatasetId(scope, key), current?.Value, current?.Version, true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>スコープやキー内の区切り文字を曖昧にしません。</summary>
    /// <returns>復号可能な内部データ識別子です。</returns>
    public static string DatasetId(string scope, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return "state:" + JsonSerializer.Serialize(new[] { scope, key });
    }
}
