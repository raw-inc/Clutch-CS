using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.Availability;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Availability;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Operational;

/// <summary>共通設定の明示未保存採用を通常の版・編集保護から分離せず扱います。</summary>
internal sealed class ClusterConfigurationAvailability(IDurableRepository repository, PeerCluster cluster, ClusterOperationalEffective effective)
{
    private string? protectedMarker;
    private bool protectedActive;

    /// <summary>未指定では旧確認済み維持を継続します。</summary>
    internal AvailabilityService? Service
    {
        get; set;
    }

    /// <summary>同じ旧activation マーカーだけでは明示候補を上書きしません。</summary>
    /// <returns>現在候補を維持するかです。</returns>
    internal bool Preserve(ClusterOperationalSnapshot desired) => protectedActive && string.Equals(desired.Revision, protectedMarker, StringComparison.Ordinal) && effective.CaptureUnsaved() is not null;

    /// <summary>保存成功だけでは現在の未保存有効版を解除しません。</summary>
    /// <returns>保存版表示の更新です。</returns>
    internal async Task RememberSavedAsync(ClusterConfigurationStored saved, CancellationToken cancellationToken = default)
    {
        if (Service is not null)
        {
            await Service.RememberConfirmedAsync(EStoragePurpose.Configuration, ClusterConfigurationStore.Id, NodeValue.FromObject(saved), saved.Snapshot.Revision, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>実採用と永続マーカー成功後にだけvolatile overrideを解除します。</summary>
    /// <returns>保存版と有効版を分離した状態更新です。</returns>
    internal async Task RememberAdoptedAsync(ClusterOperationalSnapshot adopted, CancellationToken cancellationToken = default)
    {
        protectedActive = false;
        effective.ClearUnsaved();
        if (Service is not null)
        {
            ClusterConfigurationStored? saved = await SavedAsync(cancellationToken).ConfigureAwait(false);
            ClusterConfigurationStored active = new(adopted, saved?.ActivationPolicy ?? EActivationPolicy.Explicit);
            await Service.RememberConfirmedAsync(EStoragePurpose.Configuration, ClusterConfigurationStore.Id, NodeValue.FromObject(active), adopted.Revision, true, cancellationToken).ConfigureAwait(false);
            await Service.RememberConfirmedAsync(EStoragePurpose.Configuration, ClusterConfigurationStore.Id, Value(saved), saved?.Snapshot.Revision, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>旧版再読・候補検証・保護条件確認後の保存不能だけを明示方針へ渡します。</summary>
    /// <returns>用途別候補の結果、適用対象外ならnullです。</returns>
    internal async Task<ClusterOperationalSaveResult?> FailedAsync(ClusterOperationalSaveRequest request, string? reason, CancellationToken cancellationToken = default)
    {
        bool rejected = reason is "Persistence.VersionConflict" or "Persistence.OperationMismatch" or "Persistence.ConflictSetChanged";
        if (Service is null || cluster.IsDetached || rejected || request.OperationId == Guid.Empty || !Enum.IsDefined(request.ActivationPolicy) || ClusterConfigurationValidation.Validate(request.Settings).Count != 0)
        {
            return null;
        }
        ClusterConfigurationStored? saved = await SavedAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(saved?.Snapshot.Revision, request.ExpectedRevision, StringComparison.Ordinal) || (await repository.GetConflictsAsync(ClusterConfigurationStore.SavedScope, cancellationToken).ConfigureAwait(false)).Count != 0)
        {
            return null;
        }
        SnapshotEditProtection protection = new(repository, cluster);
        if (await protection.GuardConfigurationAsync(ClusterConfigurationStore.Id, cancellationToken: cancellationToken).ConfigureAwait(false) is null)
        {
            return null;
        }
        DurableReceipt? known = await cluster.GetOperationAsync(request.OperationId, cancellationToken).ConfigureAwait(false);
        if (known?.Outcome is EOutcome.Unknown or EOutcome.Succeeded)
        {
            return null;
        }
        await Service.RememberConfirmedAsync(EStoragePurpose.Configuration, ClusterConfigurationStore.Id, Value(saved), saved?.Snapshot.Revision, cancellationToken: cancellationToken).ConfigureAwait(false);
        ClusterOperationalSnapshot snapshot = new(request.Settings, ContractJson.Hash(request.Settings), DateTimeOffset.UtcNow);
        ClusterConfigurationStored candidate = new(snapshot, request.ActivationPolicy);
        Func<NodeValue, CancellationToken, Task<EOutcome>>? apply = null;
        if (request.ActivationPolicy == EActivationPolicy.Auto)
        {
            apply = ApplyAsync;
        }
        AvailabilityWriteResult result = await Service.HandleSaveFailureAsync(EStoragePurpose.Configuration, ClusterConfigurationStore.Id, NodeValue.FromObject(candidate), snapshot.Revision, saved?.Snapshot.Revision, request.OperationId, true, apply, cancellationToken).ConfigureAwait(false);
        ConfigurationPhaseResult unshared = new(EOutcome.NotExecuted, "Availability.LocalCandidate");
        ConfigurationPhaseResult adopted = new(result.AdoptionOutcome ?? EOutcome.NotExecuted, result.Reason);
        return new(request.OperationId, snapshot.Revision, [], new(result.PersistenceOutcome, result.Reason), unshared, unshared, adopted);
    }

    /// <summary>手動候補採用でも確認方針と指定版を維持します。</summary>
    /// <returns>候補があれば採用処理自身の結果です。</returns>
    internal async Task<ConfigurationPhaseResult?> ActivateAsync(ClusterOperationalActivateRequest request, CancellationToken cancellationToken = default)
    {
        AvailabilityValue? value = Service?.TryGetCandidate(EStoragePurpose.Configuration, ClusterConfigurationStore.Id);
        ClusterConfigurationStored? candidate = value?.Value?.Json.Deserialize<ClusterConfigurationStored>(ContractJson.Options);
        if (candidate is null || !string.Equals(candidate.Snapshot.Revision, request.Revision, StringComparison.Ordinal))
        {
            return null;
        }
        if (candidate.ActivationPolicy == EActivationPolicy.ConfirmImpact && !request.ImpactConfirmed)
        {
            return new(EOutcome.NotExecuted, "Activation.ConfirmationOrRevisionMissing");
        }
        DurableEntry? marker = await repository.ReadAsync(ClusterConfigurationStore.ActivationScope, ClusterConfigurationStore.Id, cancellationToken).ConfigureAwait(false);
        if ((candidate.ActivationPolicy == EActivationPolicy.ConfirmImpact || request.ImpactToken is not null)
            && !string.Equals(request.ImpactToken, ClusterConfigurationImpact.Token(candidate.Snapshot, effective.Capture(), marker?.Version, cluster.LocalPeerId, true), StringComparison.Ordinal))
        {
            return new(EOutcome.NotExecuted, "Activation.ImpactChanged");
        }
        AvailabilityWriteResult result = await Service!.ActivateCandidateAsync(EStoragePurpose.Configuration, ClusterConfigurationStore.Id, ApplyAsync, cancellationToken).ConfigureAwait(false);
        return new(result.AdoptionOutcome ?? EOutcome.NotExecuted, result.Reason);
    }

    /// <summary>再起動時も最後に確認したprimary版と一致する代替候補だけを再検証します。</summary>
    /// <returns>明示代替媒体からの採用、または候補なしです。</returns>
    internal async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        if (Service is null)
        {
            return;
        }
        ClusterConfigurationStored? saved = await SavedAsync(cancellationToken).ConfigureAwait(false);
        ClusterOperationalSnapshot? current = effective.Capture();
        if (current is not null && Service.TryGetCurrent(EStoragePurpose.Configuration, ClusterConfigurationStore.Id) is null)
        {
            ClusterConfigurationStored retained = new(current, saved?.ActivationPolicy ?? EActivationPolicy.Explicit);
            await Service.RememberConfirmedAsync(EStoragePurpose.Configuration, ClusterConfigurationStore.Id, NodeValue.FromObject(retained), current.Revision, true, cancellationToken).ConfigureAwait(false);
        }
        await Service.RememberConfirmedAsync(EStoragePurpose.Configuration, ClusterConfigurationStore.Id, Value(saved), saved?.Snapshot.Revision, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (await Service.RestoreCandidateAsync(EStoragePurpose.Configuration, ClusterConfigurationStore.Id, cancellationToken).ConfigureAwait(false) is not null)
        {
            _ = await Service.ActivateCandidateAsync(EStoragePurpose.Configuration, ClusterConfigurationStore.Id, ApplyAsync, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>実採用直前にも共有分岐とスナップショット保護を迂回しません。</summary>
    /// <returns>未来の入力へのローカル採用結果です。</returns>
    private async Task<EOutcome> ApplyAsync(NodeValue value, CancellationToken cancellationToken = default)
    {
        ClusterConfigurationStored? candidate = value.Json.Deserialize<ClusterConfigurationStored>(ContractJson.Options);
        if (candidate is null || (await repository.GetConflictsAsync(ClusterConfigurationStore.SavedScope, cancellationToken).ConfigureAwait(false)).Count != 0)
        {
            return EOutcome.NotExecuted;
        }
        SnapshotEditProtection protection = new(repository, cluster);
        if (await protection.GuardConfigurationAsync(ClusterConfigurationStore.Id, cancellationToken: cancellationToken).ConfigureAwait(false) is null)
        {
            return EOutcome.NotExecuted;
        }
        ClusterOperationalSnapshot? marker = (await repository.ReadAsync(ClusterConfigurationStore.ActivationScope, ClusterConfigurationStore.Id, cancellationToken).ConfigureAwait(false))?.Value?.Json.Deserialize<ClusterOperationalSnapshot>(ContractJson.Options);
        ConfigurationPhaseResult result = await effective.ApplyUnsavedAsync(candidate.Snapshot, cancellationToken).ConfigureAwait(false);
        if (result.Outcome == EOutcome.Succeeded)
        {
            protectedMarker = marker?.Revision;
            protectedActive = true;
        }
        return result.Outcome;
    }

    /// <summary>現在のprimary保存版だけを取得します。</summary>
    /// <returns>未保存候補を混ぜない確認値です。</returns>
    private async Task<ClusterConfigurationStored?> SavedAsync(CancellationToken cancellationToken = default) => (await repository.ReadAsync(ClusterConfigurationStore.SavedScope, ClusterConfigurationStore.Id, cancellationToken).ConfigureAwait(false))?.Value?.Json.Deserialize<ClusterConfigurationStored>(ContractJson.Options);

    /// <summary>未作成と保存用データを区別します。</summary>
    /// <returns>用途キャッシュへ渡す保存値です。</returns>
    private static NodeValue? Value(ClusterConfigurationStored? stored)
    {
        if (stored is null)
        {
            return null;
        }
        return NodeValue.FromObject(stored);
    }
}
