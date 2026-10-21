using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Membership;

/// <summary>明示初期化、永続復旧点と人の離脱を区別します。</summary>
internal sealed class PeerRecovery : IDisposable
{
    private readonly ClusterOptions options;
    private readonly IDurableRepository repository;
    private readonly PeerMembership membership;
    private readonly PeerKnownMembers known;
    private readonly SemaphoreSlim transitions = new(1, 1);
    private Func<CancellationToken, ValueTask<bool>>? readiness;
    private bool trustedRecovery;
    private bool initialCreationAuthorized;

    /// <summary>全候補取得後の依存範囲隔離をCoreが確認する入口です。</summary>
    internal Func<CancellationToken, ValueTask<bool>>? ConflictReadinessCheck
    {
        get; set;
    }

    /// <summary>参加状態と永続マーカーを接続します。</summary>
    public PeerRecovery(ClusterOptions options, IDurableRepository repository, PeerMembership membership)
    {
        this.options = options;
        this.repository = repository;
        this.membership = membership;
        known = new PeerKnownMembers(options, repository, membership);
    }

    /// <summary>実必要条件の確認処理です。</summary>
    public Func<CancellationToken, ValueTask<bool>>? ReadinessCheck
    {
        get => CheckReadyAsync;
        set => readiness = value;
    }

    /// <summary>人が明示離脱させたか。</summary>
    public bool Detached
    {
        get; private set;
    }

    /// <summary>明示初期化と検証済み復旧点以外は同期を待ちます。</summary>
    /// <returns>初期状態の確定。</returns>
    public Task InitializeAsync(CancellationToken cancellationToken = default) => TransitionAsync(InitializeCoreAsync, cancellationToken);

    /// <summary>初期の意図と復旧マーカーを同じ排他内で読みます。</summary>
    /// <returns>初期状態の確定です。</returns>
    private async Task InitializeCoreAsync(CancellationToken cancellationToken = default)
    {
        DurableEntry? marker = await repository.ReadAsync("cluster:" + options.Id, options.PeerId, cancellationToken).ConfigureAwait(false);
        await known.InitializeAsync(marker is not null, cancellationToken).ConfigureAwait(false);
        Detached = marker?.Value?.GetString() is "detached";

        // 復旧の参加条件は現在値と未確定予約です。過去本文の検証は、その本文を使用する照会・同期へ限定します。
        _ = await repository.GetSharedVersionAsync(cancellationToken).ConfigureAwait(false);
        membership.SetLocal(EPeerStatus.Syncing);
        if (Detached)
        {
            membership.SetLocal(EPeerStatus.Detached);
            return;
        }
        string trusted = "trusted";
        trustedRecovery = string.Equals(marker?.Value?.GetString(), trusted, StringComparison.Ordinal);
        initialCreationAuthorized = options.NewCluster && marker is null;
        if (initialCreationAuthorized)
        {
            await BecomeReadyCoreAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>発見猶予後、全停止からの信頼可能な一台復旧だけを許します。</summary>
    /// <returns>復旧確認。</returns>
    public Task RecoverAloneAsync(CancellationToken cancellationToken = default) => TransitionAsync(RecoverAloneCoreAsync, cancellationToken);

    /// <summary>ローカルの参加意図を変更させずに単独復旧を検証します。</summary>
    /// <returns>復旧確認です。</returns>
    private async Task RecoverAloneCoreAsync(CancellationToken cancellationToken = default)
    {
        EPeerStatus local = membership.Snapshot().Single(peer => string.Equals(peer.PeerId, options.PeerId, StringComparison.Ordinal)).Status;
        if ((trustedRecovery || initialCreationAuthorized) && !Detached && local is EPeerStatus.Syncing or EPeerStatus.Unavailable && !membership.Snapshot().Any(peer => !string.Equals(peer.PeerId, options.PeerId, StringComparison.Ordinal) && PeerStatusRules.CanParticipate(peer.Status)))
        {
            if (!await CheckReadyAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }
            _ = await repository.GetSharedVersionAsync(cancellationToken).ConfigureAwait(false);
            await BecomeReadyCoreAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>必要条件の回復後も通常再参加をせず、明示離脱のローカル利用だけを戻します。</summary>
    /// <returns>実際の必要条件を確認したローカル状態更新です。</returns>
    public ValueTask CheckAvailabilityAsync(CancellationToken cancellationToken = default) => new(TransitionAsync(CheckAvailabilityCoreAsync, cancellationToken));

    /// <summary>必要条件の待機を挟んでもローカル遷移を交差させません。</summary>
    /// <returns>現在の意図に対応する状態確認です。</returns>
    private async Task CheckAvailabilityCoreAsync(CancellationToken cancellationToken = default)
    {
        if (!await CheckReadyAsync(cancellationToken).ConfigureAwait(false))
        {
            membership.SetLocal(EPeerStatus.Unavailable);
        }
        else
        {
            if (Detached)
            {
                membership.SetLocal(EPeerStatus.Detached);
            }
            else
            {
                EPeerStatus status = membership.Snapshot().Single(peer => peer.InstanceId == membership.InstanceId).Status;
                int conflicts = (await repository.GetConflictsAsync(string.Empty, cancellationToken).ConfigureAwait(false)).Count;
                if (status == EPeerStatus.Conflict && conflicts == 0 && !membership.IdentityConflict)
                {
                    // 内容の解消だけではReadyへ進めず、相手との同期確認を再開します。
                    membership.SetLocal(EPeerStatus.Syncing);
                }
                if ((status == EPeerStatus.Degraded && conflicts == 0) || (status == EPeerStatus.Conflict && conflicts != 0 && !membership.IdentityConflict && ConflictReadinessCheck is not null))
                {
                    await BecomeReadyCoreAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>明示離脱を保存します。</summary>
    /// <returns>離脱の完了。</returns>
    public Task DetachAsync(CancellationToken cancellationToken = default) => TransitionAsync(DetachCoreAsync, cancellationToken);

    /// <summary>明示離脱とその永続マーカーを一つの遷移で保存します。</summary>
    /// <returns>離脱保存の完了です。</returns>
    private async Task DetachCoreAsync(CancellationToken cancellationToken = default)
    {
        Detached = true;
        membership.SetLocal(EPeerStatus.Detached);
        await SaveMarkerAsync("detached", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>再参加は実同期を経由します。</summary>
    /// <returns>再参加開始。</returns>
    public Task RejoinAsync(CancellationToken cancellationToken = default) => TransitionAsync(RejoinCoreAsync, cancellationToken);

    /// <summary>同期の再開とマーカーを一つの遷移にします。</summary>
    /// <returns>再参加開始の保存です。</returns>
    private async Task RejoinCoreAsync(CancellationToken cancellationToken = default)
    {
        Detached = false;
        trustedRecovery = false;
        initialCreationAuthorized = false;
        membership.SetLocal(EPeerStatus.Syncing);
        await SaveMarkerAsync("rejoining", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>実必要条件を満たした復旧点だけを保存します。</summary>
    /// <returns>状態確認の完了。</returns>
    public Task BecomeReadyAsync(CancellationToken cancellationToken = default) => TransitionAsync(BecomeReadyCoreAsync, cancellationToken);

    /// <summary>確認待ちに離脱マーカーを書き換えず、通常参加を保存します。</summary>
    /// <returns>準備と保存の完了です。</returns>
    private async Task BecomeReadyCoreAsync(CancellationToken cancellationToken = default)
    {
        bool contentConflict = (await repository.GetConflictsAsync(string.Empty, cancellationToken).ConfigureAwait(false)).Count != 0;
        bool isolated = !contentConflict || (ConflictReadinessCheck is not null && await ConflictReadinessCheck(cancellationToken).ConfigureAwait(false));
        if (!membership.IdentityConflict && isolated && !Detached && ReadinessCheck is not null && await ReadinessCheck(cancellationToken).ConfigureAwait(false))
        {
            await SaveMarkerAsync("trusted", cancellationToken).ConfigureAwait(false);
            trustedRecovery = true;
            initialCreationAuthorized = false;
            EPeerStatus status = EPeerStatus.Ready;
            if (contentConflict)
            {
                status = EPeerStatus.Degraded;
            }
            membership.SetLocal(status);
        }
    }

    /// <summary>人の離脱と検証済み復旧点を保存します。</summary>
    /// <returns>保存完了。</returns>
    private async Task SaveMarkerAsync(string value, CancellationToken cancellationToken = default)
    {
        string scope = "cluster:" + options.Id;
        DurableEntry? current = await repository.ReadAsync(scope, options.PeerId, cancellationToken).ConfigureAwait(false);
        if (string.Equals(current?.Value?.GetString(), value, StringComparison.Ordinal))
        {
            return;
        }
        DurableReceipt receipt = await repository.ApplyBatchAsync(Guid.CreateVersion7(), [new DurableMutation(scope, options.PeerId, current?.Version, NodeValue.FromObject(value))], cancellationToken).ConfigureAwait(false);
        if (receipt.Outcome != EOutcome.Succeeded)
        {
            throw new InvalidDataException("参加状態を保存できません。");
        }
    }

    /// <summary>既知集合を保存できない状態を通常参加としません。</summary>
    /// <returns>ローカル記録と外側の実必要条件が揃ったか。</returns>
    private async ValueTask<bool> CheckReadyAsync(CancellationToken cancellationToken = default)
    {
        if (!await PersistKnownAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }
        return readiness is not null && await readiness(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>受理した参加事実の変化を保持してから通常処理へ進めます。</summary>
    /// <returns>現在の既知集合が保存済みか。</returns>
    public async ValueTask<bool> PersistKnownAsync(CancellationToken cancellationToken = default)
    {
        if (!await known.FlushAsync(cancellationToken).ConfigureAwait(false))
        {
            membership.SetLocal(EPeerStatus.Unavailable);
            return false;
        }
        return true;
    }

    /// <summary>ローカル既知集合の同期資源を解放します。</summary>
    public void Dispose()
    {
        known.Dispose();
        transitions.Dispose();
    }

    /// <summary>ローカル遷移を直列化し、内部呼出しでは再取得しません。</summary>
    /// <returns>永続マーカーを含む一つの遷移の完了です。</returns>
    private async Task TransitionAsync(Func<CancellationToken, Task> transition, CancellationToken cancellationToken = default)
    {
        await transitions.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await transition(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            transitions.Release();
        }
    }
}
