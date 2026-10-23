using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Operational;

/// <summary>このPeerで確認済みの共通設定だけを未来の入力へ公開します。</summary>
internal sealed class ClusterOperationalEffective(IDurableRepository repository, string peerId)
{
    private ClusterOperationalSnapshot? effective;
    private ClusterOperationalSnapshot? unsaved;

    /// <summary>明示未保存候補を旧反映マーカーで上書きしない境界です。</summary>
    internal Func<ClusterOperationalSnapshot, bool>? PreserveUnsaved
    {
        get; set;
    }

    /// <summary>ローカル永続採用が成功した後の状態表示を更新します。</summary>
    internal Func<ClusterOperationalSnapshot, CancellationToken, Task>? Adopted
    {
        get; set;
    }

    /// <summary>明示Bの有効候補を通常永続版とは別に返します。</summary>
    /// <returns>現在の未保存候補です。</returns>
    internal ClusterOperationalSnapshot? CaptureUnsaved()
    {
        ClusterOperationalSnapshot? current = Volatile.Read(ref unsaved);
        if (current is null)
        {
            return null;
        }
        return ContractJson.Copy(current);
    }

    /// <summary>保存不能時の明示候補を未来の入力へ採用し、永続マーカーを書きません。</summary>
    /// <returns>実採用結果です。永続保存の成功ではありません。</returns>
    internal Task<ConfigurationPhaseResult> ApplyUnsavedAsync(ClusterOperationalSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ClusterOperationalSnapshot? projected = snapshot;
        if (ConflictProjection is not null)
        {
            projected = ConflictProjection(snapshot);
        }
        if (projected is null || 0 < projected.UnresolvedFields?.Count || !string.Equals(projected.Revision, snapshot.Revision, StringComparison.Ordinal) || ClusterConfigurationValidation.Validate(snapshot.Settings).Count != 0 || !string.Equals(snapshot.Revision, ContractJson.Hash(snapshot.Settings), StringComparison.Ordinal))
        {
            return Task.FromResult(new ConfigurationPhaseResult(EOutcome.NotExecuted, "ClusterConfiguration.UnsavedCandidateInvalid"));
        }
        Volatile.Write(ref unsaved, ContractJson.Copy(snapshot));
        return Task.FromResult(new ConfigurationPhaseResult(EOutcome.Succeeded, "Availability.UnsavedEffective"));
    }

    /// <summary>確認済み通常版を実採用した後でだけ未保存overrideを解除します。</summary>
    internal void ClearUnsaved() => Volatile.Write(ref unsaved, null);

    /// <summary>候補を任意採用せず、検証済み非依存部分の派生版へ投影します。</summary>
    internal Func<ClusterOperationalSnapshot, ClusterOperationalSnapshot?>? ConflictProjection
    {
        get; set;
    }

    /// <summary>通常保存・復元と同じローカル排他内で採用を確認します。</summary>
    /// <returns>ローカル準備結果です。</returns>
    public async Task<LocalPreparationResult> PrepareSynchronizedAsync(SemaphoreSlim mutex, Func<string, CancellationToken, Task<bool>> hasSnapshot, CancellationToken cancellationToken = default)
    {
        await mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await PrepareAsync(hasSnapshot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            mutex.Release();
        }
    }

    /// <summary>共有書込やReadyを要求せず、確認済み反映希望だけをローカル採用します。</summary>
    /// <returns>空と未確認を区別した準備結果です。</returns>
    public async Task<LocalPreparationResult> PrepareAsync(Func<string, CancellationToken, Task<bool>> hasSnapshot, CancellationToken cancellationToken = default)
    {
        DurableEntry? marker = await repository.ReadAsync(ClusterConfigurationStore.ActivationScope, ClusterConfigurationStore.Id, cancellationToken).ConfigureAwait(false);

        // 保存候補は未作成の判定にだけ使い、反映希望がある通常巡回では読みません。
        if (marker is null && await repository.ReadAsync(ClusterConfigurationStore.SavedScope, ClusterConfigurationStore.Id, cancellationToken).ConfigureAwait(false) is null)
        {
            return new(ELocalPreparationState.Empty, [], "ClusterConfiguration.Empty");
        }
        ClusterOperationalSnapshot? candidate;
        try
        {
            candidate = marker?.Value?.Json.Deserialize<ClusterOperationalSnapshot>();
        }
        catch (JsonException)
        {
            return new(ELocalPreparationState.Unavailable, [], "ClusterConfiguration.InvalidSnapshot");
        }
        if (candidate?.Settings is null)
        {
            return new(ELocalPreparationState.Unavailable, [], "ClusterConfiguration.NoConfirmedActivation");
        }
        IReadOnlyList<ValidationIssue> issues = ClusterConfigurationValidation.Validate(candidate.Settings);
        if (issues.Count != 0 || !string.Equals(candidate.Revision, ContractJson.Hash(candidate.Settings), StringComparison.Ordinal))
        {
            return new(ELocalPreparationState.Unavailable, issues, "ClusterConfiguration.InvalidSnapshot");
        }
        ClusterOperationalSnapshot? volatileCandidate = CaptureUnsaved();
        if (volatileCandidate is not null && PreserveUnsaved?.Invoke(candidate) == true)
        {
            ClusterOperationalSnapshot? projected = volatileCandidate;
            if (ConflictProjection is not null)
            {
                projected = ConflictProjection(volatileCandidate);
            }
            if (projected is not null && string.Equals(projected.Revision, volatileCandidate.Revision, StringComparison.Ordinal) && (projected.UnresolvedFields?.Count ?? 0) == 0)
            {
                return new(ELocalPreparationState.Prepared, [], "Availability.UnsavedEffective");
            }
        }
        string confirmationRevision = candidate.Revision;
        if (ConflictProjection is not null)
        {
            candidate = ConflictProjection(candidate);
            if (candidate is null)
            {
                return new(ELocalPreparationState.Unavailable, [], "ClusterConfiguration.ConflictProjectionUnconfirmed");
            }
        }
        ClusterOperationalSnapshot? current = Capture();
        if (string.Equals(current?.Revision, candidate.Revision, StringComparison.Ordinal)
            && string.Equals(ContractJson.Hash(current?.UnresolvedFields), ContractJson.Hash(candidate.UnresolvedFields), StringComparison.Ordinal))
        {
            return new(ELocalPreparationState.Prepared, []);
        }
        if (!await hasSnapshot(confirmationRevision, cancellationToken).ConfigureAwait(false))
        {
            return new(ELocalPreparationState.Unavailable, [], "ClusterConfiguration.NoConfirmedActivation");
        }
        ConfigurationPhaseResult result = await ApplyAsync(candidate, cancellationToken).ConfigureAwait(false);
        if (result.Outcome != EOutcome.Succeeded)
        {
            return new(ELocalPreparationState.Unavailable, [], result.Reason);
        }
        return new(ELocalPreparationState.Prepared, []);
    }

    /// <summary>再起動前に確認済みだったローカル版だけを読みます。</summary>
    /// <returns>ローカル状態の取得完了です。</returns>
    public async Task ReadRetainedAsync(CancellationToken cancellationToken = default)
    {
        DurableEntry? retained = await repository.ReadAsync("peer:" + peerId + ":operational-effective", ClusterConfigurationStore.Id, cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref effective, ClusterConfigurationValidation.ReadConfirmed(retained?.Value));
    }

    /// <summary>開始前に有効版がなかった状態へ明示復元します。</summary>
    /// <returns>ローカル解除の確認です。</returns>
    public async Task<ConfigurationPhaseResult> ClearAsync(CancellationToken cancellationToken = default)
    {
        EOutcome cleared = await repository.WriteLocalSnapshotAsync("peer:" + peerId + ":operational-effective", ClusterConfigurationStore.Id, NodeValue.Null, cancellationToken).ConfigureAwait(false);
        if (cleared == EOutcome.Succeeded)
        {
            Volatile.Write(ref effective, null);
            ClearUnsaved();
        }
        return new(cleared);
    }

    /// <summary>一件の所有コピーを返し、公開後の設定変更を遡及させません。</summary>
    /// <returns>現在有効な版、または初期化未成立のnullです。</returns>
    public ClusterOperationalSnapshot? Capture()
    {
        ClusterOperationalSnapshot? volatileCandidate = CaptureUnsaved();
        if (volatileCandidate is not null)
        {
            ClusterOperationalSnapshot? projected = volatileCandidate;
            if (ConflictProjection is not null)
            {
                projected = ConflictProjection(volatileCandidate);
            }
            if (projected is not null && string.Equals(projected.Revision, volatileCandidate.Revision, StringComparison.Ordinal) && (projected.UnresolvedFields?.Count ?? 0) == 0)
            {
                return volatileCandidate;
            }
        }
        ClusterOperationalSnapshot? current = Volatile.Read(ref effective);
        if (current is null)
        {
            return null;
        }
        return ContractJson.Copy(current);
    }

    /// <summary>検証とローカル永続保存が成功してからサービス提供元を差し替えます。</summary>
    /// <returns>未来の入力への適用確認です。</returns>
    public async Task<ConfigurationPhaseResult> ApplyAsync(ClusterOperationalSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        if (ClusterConfigurationValidation.Validate(snapshot.Settings).Count != 0 || !string.Equals(snapshot.Revision, ContractJson.Hash(snapshot.Settings), StringComparison.Ordinal))
        {
            return new(EOutcome.NotExecuted, "ClusterConfiguration.InvalidSnapshot");
        }
        EOutcome saved = await repository.WriteLocalSnapshotAsync("peer:" + peerId + ":operational-effective", ClusterConfigurationStore.Id, NodeValue.FromObject(snapshot), cancellationToken).ConfigureAwait(false);
        if (saved == EOutcome.Succeeded)
        {
            Volatile.Write(ref effective, ContractJson.Copy(snapshot));
            ClearUnsaved();
            if (Adopted is not null)
            {
                await Adopted(snapshot, cancellationToken).ConfigureAwait(false);
            }
        }
        return new(saved);
    }

}
