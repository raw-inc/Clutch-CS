using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Availability;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Operational;

/// <summary>一つの共有運用設定を保存し、スナップショット成功後だけ未来の入力へ公開します。</summary>
public sealed class ClusterConfigurationStore : IClusterOperationalProvider, IDisposable
{
    private readonly IDurableRepository repository;
    private readonly PeerCluster cluster;
    private readonly SnapshotCatalog snapshots;
    private readonly ConfigurationOperationJournal journal;
    private readonly SnapshotEditProtection protection;
    private readonly SemaphoreSlim mutex = new(1, 1);
    private readonly ClusterOperationalEffective effective;
    private readonly ClusterConfigurationAvailability availability;
    private readonly ClusterConfigurationSaving saving;
    internal static string SavedScope => "cluster.operational";
    internal static string HistoryScope => "cluster.operational.history";
    internal static string ActivationScope => "cluster.operational.activation";
    internal static string SaveKind => "operational-save";
    private static string ActivateKind => "operational-activate";

    /// <summary>既存の共有リポジトリとスナップショット保管を共用します。</summary>
    public ClusterConfigurationStore(IDurableRepository repository, PeerCluster cluster)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(cluster);
        this.repository = repository;
        this.cluster = cluster;
        snapshots = new SnapshotCatalog(repository, cluster);
        journal = new ConfigurationOperationJournal(repository);
        protection = new SnapshotEditProtection(repository, cluster);
        effective = new ClusterOperationalEffective(repository, cluster.LocalPeerId);
        availability = new(repository, cluster, effective);
        effective.PreserveUnsaved = availability.Preserve;
        effective.Adopted = availability.RememberAdoptedAsync;
        saving = new(repository, cluster, snapshots, journal, protection, availability, Capture, ActivateCoreAsync);
        SnapshotParticipant = new ClusterConfigurationSnapshotParticipant(this, snapshots);
    }

    /// <summary>用途別の未保存候補方針と全画面メタデータを接続します。</summary>
    public AvailabilityService? Availability
    {
        get => availability.Service;
        set => availability.Service = value;
    }

    /// <summary>Configuration kindで登録するスナップショット参加者です。</summary>
    public ClusterConfigurationSnapshotParticipant SnapshotParticipant
    {
        get;
    }

    /// <summary>ワークフロー名と衝突しない共通設定スナップショット対象です。</summary>
    public static string Id => "cluster-operational";

    /// <summary>スナップショット復元が対象を保護している間は通常保存を拒否します。</summary>
    public Func<bool>? IsRestoreLocked
    {
        get; set;
    }

    /// <summary>一件の所有コピーを返します。</summary>
    /// <returns>このPeerの実有効設定です。</returns>
    public ClusterOperationalSnapshot? Capture() => effective.Capture();

    /// <summary>共有分岐の安全な部分だけを有効版へ投影する境界です。</summary>
    internal Func<ClusterOperationalSnapshot, ClusterOperationalSnapshot?>? ConflictProjection
    {
        get => effective.ConflictProjection;
        set => effective.ConflictProjection = value;
    }

    /// <summary>同期済みの共有版をReady前にローカル採用し、初回seedは行いません。</summary>
    /// <returns>確認済み・未作成・未確認を区別した結果です。</returns>
    public Task<LocalPreparationResult> PrepareLocalAsync(CancellationToken cancellationToken = default) => effective.PrepareSynchronizedAsync(mutex, SnapshotParticipant.HasSavedRevisionAsync, cancellationToken);

    /// <summary>既存共有版を優先し、明示新クラスタだけがTOMLの初期値をseedします。</summary>
    /// <returns>このPeerの設定利用可否です。</returns>
    public async Task<ConfigurationPhaseResult> InitializeAsync(ClusterOperationalSettings seed, bool allowSeed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(seed);
        if (Capture() is null)
        {
            await effective.ReadRetainedAsync(cancellationToken).ConfigureAwait(false);
            await availability.RestoreAsync(cancellationToken).ConfigureAwait(false);
        }
        ClusterConfigurationStored? saved = await ReadStoredAsync(SavedScope, Id, cancellationToken).ConfigureAwait(false);
        if (saved is null && allowSeed)
        {
            ClusterOperationalSaveResult result = await SaveAsync(new(WorkflowStore.PhaseId(Guid.Empty, Id + ".seed"), seed, null), cancellationToken).ConfigureAwait(false);
            if (result.Activation.Outcome != EOutcome.Succeeded)
            {
                return result.Activation;
            }
        }
        return await ReconcileAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>保存候補とローカル実有効版を分けて読みます。</summary>
    /// <returns>表示・スナップショット用の二つの版です。</returns>
    public async Task<ClusterOperationalState> GetAsync(CancellationToken cancellationToken = default)
    {
        ClusterConfigurationStored? saved = await ReadStoredAsync(SavedScope, Id, cancellationToken).ConfigureAwait(false);
        if (saved is not null)
        {
            await availability.RememberSavedAsync(saved, cancellationToken).ConfigureAwait(false);
        }
        return new(saved?.Snapshot, Capture(), saved?.ActivationPolicy ?? EActivationPolicy.Explicit);
    }

    /// <summary>現在の候補と有効版の確認情報を取得します。</summary>
    /// <returns>版固定した影響情報です。</returns>
    public Task<ConfigurationImpactPreview> InspectImpactAsync(CancellationToken cancellationToken = default)
        => ClusterConfigurationImpact.InspectAsync(this, repository, cluster.LocalPeerId, cancellationToken);

    /// <summary>同じ保存要求を再適用せず既知の段階結果を読みます。</summary>
    /// <returns>保存結果、未受理ならnullです。</returns>
    public async Task<ClusterOperationalSaveResult?> GetOperationAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        ConfigurationOperation? operation = await journal.ReadAsync(SaveKind, operationId, cancellationToken).ConfigureAwait(false);
        if (operation is null)
        {
            return null;
        }
        return operation.Result?.Deserialize<ClusterOperationalSaveResult>() ?? ClusterConfigurationValidation.Unstarted(operationId, EOutcome.Unknown, "ClusterConfiguration.Interrupted");
    }

    /// <summary>共有とスナップショットが成立した版だけを自動反映します。</summary>
    /// <returns>保存、共有、スナップショット、ローカル実適用の個別結果です。</returns>
    public async Task<ClusterOperationalSaveResult> SaveAsync(ClusterOperationalSaveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = ContractJson.Copy(request);
        await mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRestoreLocked?.Invoke() == true)
            {
                return ClusterConfigurationValidation.Unstarted(request.OperationId, EOutcome.NotExecuted, "ClusterConfiguration.RestoreLocked");
            }
            ConfigurationOperation? previous = await journal.ReadAsync(SaveKind, request.OperationId, cancellationToken).ConfigureAwait(false);
            if (previous is not null)
            {
                if (!string.Equals(previous.Fingerprint, ContractJson.Hash(request), StringComparison.Ordinal))
                {
                    return ClusterConfigurationValidation.Unstarted(request.OperationId, EOutcome.Failed, "ClusterConfiguration.OperationMismatch");
                }
                return previous.Result?.Deserialize<ClusterOperationalSaveResult>() ?? ClusterConfigurationValidation.Unstarted(request.OperationId, EOutcome.Unknown, "ClusterConfiguration.Interrupted");
            }
            if (!await journal.BeginAsync(SaveKind, request.OperationId, request, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                return ClusterConfigurationValidation.Unstarted(request.OperationId, EOutcome.Unknown, "ClusterConfiguration.BeginUnconfirmed");
            }
            ClusterOperationalSaveResult result = await saving.SaveAsync(request, cancellationToken).ConfigureAwait(false);
            await journal.CompleteAsync(SaveKind, request.OperationId, result, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Data.Common.DbException)
        {
            ClusterOperationalSaveResult? candidate = await availability.FailedAsync(request, "Persistence.WriteFailed", CancellationToken.None).ConfigureAwait(false);
            return candidate ?? await GetOperationAsync(request.OperationId, CancellationToken.None).ConfigureAwait(false) ?? ClusterConfigurationValidation.Unstarted(request.OperationId, EOutcome.Unknown, "ClusterConfiguration.OperationUnconfirmed");
        }
        catch (OperationCanceledException)
        {
            return await GetOperationAsync(request.OperationId, CancellationToken.None).ConfigureAwait(false) ?? ClusterConfigurationValidation.Unstarted(request.OperationId, EOutcome.Unknown, "ClusterConfiguration.OperationUnconfirmed");
        }
        finally
        {
            mutex.Release();
        }
    }

    /// <summary>明示適用も操作IDと保存時スナップショットの成立を確認します。</summary>
    /// <returns>このPeerの実適用結果です。</returns>
    public async Task<ConfigurationPhaseResult> ActivateAsync(ClusterOperationalActivateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRestoreLocked?.Invoke() == true)
            {
                return new(EOutcome.NotExecuted, "ClusterConfiguration.RestoreLocked");
            }
            ConfigurationOperation? previous = await journal.ReadAsync(ActivateKind, request.OperationId, cancellationToken).ConfigureAwait(false);
            if (previous is not null)
            {
                if (!string.Equals(previous.Fingerprint, ContractJson.Hash(request), StringComparison.Ordinal))
                {
                    return new(EOutcome.Failed, "ClusterConfiguration.OperationMismatch");
                }
                return previous.Result?.Deserialize<ConfigurationPhaseResult>() ?? new(EOutcome.Unknown, "ClusterConfiguration.Interrupted");
            }
            if (!await journal.BeginAsync(ActivateKind, request.OperationId, request, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                return new(EOutcome.Unknown, "ClusterConfiguration.BeginUnconfirmed");
            }
            ConfigurationPhaseResult? candidate = await availability.ActivateAsync(request, cancellationToken).ConfigureAwait(false);
            ConfigurationPhaseResult result = candidate ?? await ActivateCoreAsync(request, cancellationToken).ConfigureAwait(false);
            await journal.CompleteAsync(ActivateKind, request.OperationId, result, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            mutex.Release();
        }
    }

    /// <summary>確認済み版の反映希望を共有してから未来のローカル入力へ公開します。</summary>
    /// <returns>共有またはローカル反映の実行結果です。</returns>
    private async Task<ConfigurationPhaseResult> ActivateCoreAsync(ClusterOperationalActivateRequest request, CancellationToken cancellationToken = default)
    {
        ClusterConfigurationStored? candidate = await ReadStoredAsync(HistoryScope, request.Revision, cancellationToken).ConfigureAwait(false);
        ClusterConfigurationStored? current = await ReadStoredAsync(SavedScope, Id, cancellationToken).ConfigureAwait(false);
        if (string.Equals(current?.Snapshot.Revision, request.Revision, StringComparison.Ordinal))
        {
            candidate = current;
        }
        if (candidate is null || (candidate.ActivationPolicy == EActivationPolicy.ConfirmImpact && !request.ImpactConfirmed) || !await SnapshotParticipant.HasSavedRevisionAsync(request.Revision, cancellationToken).ConfigureAwait(false))
        {
            return new(EOutcome.NotExecuted, "ClusterConfiguration.SnapshotOrConfirmationMissing");
        }
        DurableEntry? marker = await repository.ReadAsync(ActivationScope, Id, cancellationToken).ConfigureAwait(false);
        DurableMutation? guard = await protection.GuardConfigurationAsync(Id, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (guard is null)
        {
            return new(EOutcome.NotExecuted, "ClusterConfiguration.RestoreLocked");
        }
        current = await ReadStoredAsync(SavedScope, Id, cancellationToken).ConfigureAwait(false);
        if (string.Equals(current?.Snapshot.Revision, request.Revision, StringComparison.Ordinal))
        {
            candidate = current!;
        }
        if (candidate.ActivationPolicy == EActivationPolicy.ConfirmImpact && !request.ImpactConfirmed)
        {
            return new(EOutcome.NotExecuted, "ClusterConfiguration.SnapshotOrConfirmationMissing");
        }
        if ((candidate.ActivationPolicy == EActivationPolicy.ConfirmImpact || request.ImpactToken is not null)
            && (!string.Equals(current?.Snapshot.Revision, request.Revision, StringComparison.Ordinal)
            || !string.Equals(request.ImpactToken, ClusterConfigurationImpact.Token(candidate.Snapshot, Capture(), marker?.Version, cluster.LocalPeerId), StringComparison.Ordinal)))
        {
            return new(EOutcome.NotExecuted, "Activation.ImpactChanged");
        }
        DurableReceipt shared = await cluster.ReplicateAsync(request.OperationId, [new DurableMutation(ActivationScope, Id, marker?.Version, NodeValue.FromObject(candidate.Snapshot)), guard], cancellationToken).ConfigureAwait(false);
        if (shared.Outcome != EOutcome.Succeeded)
        {
            return new(shared.Outcome, shared.Reason);
        }
        return await effective.ApplyAsync(candidate.Snapshot, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>共有済み反映希望だけを読み、ローカルTOMLの相違で上書きしません。</summary>
    /// <returns>このPeerで新規入力に使えるかです。</returns>
    public async Task<ConfigurationPhaseResult> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        await mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRestoreLocked?.Invoke() == true || !cluster.GetPeers().Any(peer => peer.InstanceId == cluster.LocalInstanceId && PeerStatusRules.CanParticipate(peer.Status)))
            {
                return new(EOutcome.NotExecuted, "ClusterConfiguration.NotReady");
            }
            LocalPreparationResult result = await effective.PrepareAsync(SnapshotParticipant.HasSavedRevisionAsync, cancellationToken).ConfigureAwait(false);
            if (result.State == ELocalPreparationState.Prepared)
            {
                return new(EOutcome.Succeeded);
            }
            return new(EOutcome.NotExecuted, result.Reason);
        }
        finally
        {
            mutex.Release();
        }
    }

    /// <summary>同じ保存形式を保存版と履歴版で共用します。</summary>
    /// <returns>存在する保存版です。</returns>
    private async Task<ClusterConfigurationStored?> ReadStoredAsync(string scope, string key, CancellationToken cancellationToken = default) => (await repository.ReadAsync(scope, key, cancellationToken).ConfigureAwait(false))?.Value?.Json.Deserialize<ClusterConfigurationStored>();

    /// <summary>設定スナップショットとして保存版と実有効版を同じ排他内で取得します。</summary>
    /// <returns>実際に確認した設定状態です。</returns>
    internal async Task<NodeValue> CaptureStateAsync(CancellationToken cancellationToken = default)
    {
        await mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return NodeValue.FromObject(await GetAsync(cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            mutex.Release();
        }
    }

    /// <summary>外側スナップショット操作の一段階として保存版と有効希望を原子的に戻します。</summary>
    /// <returns>共有変更とローカル実復元の結果です。</returns>
    public Task<ConfigurationPhaseResult> RestoreSnapshotAsync(NodeValue snapshot, CancellationToken cancellationToken = default) => RestoreSnapshotCoreAsync(snapshot, null, cancellationToken);

    /// <summary>元復元操作の保護を検証して設定を戻します。</summary>
    /// <returns>共有とローカル適用の実行結果です。</returns>
    internal Task<ConfigurationPhaseResult> RestoreOwnedSnapshotAsync(Guid owner, NodeValue snapshot, CancellationToken cancellationToken = default) => RestoreSnapshotCoreAsync(snapshot, owner, cancellationToken);

    /// <summary>通常の直接復元も保護を迂回せず、外側操作だけが所有保護を使用します。</summary>
    /// <returns>共有変更とローカル実復元の結果です。</returns>
    private async Task<ConfigurationPhaseResult> RestoreSnapshotCoreAsync(NodeValue snapshot, Guid? owner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if ((await SnapshotParticipant.ValidateAsync(snapshot, cancellationToken).ConfigureAwait(false)).Count != 0)
        {
            return new(EOutcome.NotExecuted, "ClusterConfiguration.InvalidSnapshot");
        }
        ClusterOperationalState state = snapshot.Json.Deserialize<ClusterOperationalState>()!;
        await mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DurableMutation? guard = await protection.GuardConfigurationAsync(Id, owner, cancellationToken).ConfigureAwait(false);
            if (guard is null)
            {
                return new(EOutcome.NotExecuted, "ClusterConfiguration.RestoreLocked");
            }
            ConfigurationPhaseResult restored = await ClusterConfigurationSnapshotParticipant.RestoreSavedAsync(state, repository, cluster, guard, cancellationToken).ConfigureAwait(false);
            if (restored.Outcome != EOutcome.Succeeded)
            {
                return restored;
            }
            if (state.Effective is not null)
            {
                return await effective.ApplyAsync(state.Effective, cancellationToken).ConfigureAwait(false);
            }
            return await effective.ClearAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            mutex.Release();
        }
    }

    /// <summary>所有する非同期排他を解放します。</summary>
    public void Dispose() => mutex.Dispose();
}
