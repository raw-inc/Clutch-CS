using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Causality;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Replication;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Replication;

/// <summary>操作開始時の実体集合から確認条件を縮小しない複製です。</summary>
internal sealed class ClusterReplication : IDisposable
{
    private readonly PeerCluster cluster;
    private readonly IDurableRepository repository;
    private readonly ReplicationPreparationCapabilities preparationCapabilities;
    private readonly ReplicationJournal journal;
    private readonly ReplicationRecovery recovery;
    private readonly int maximumBytes;
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>同じ通常UDP配送と保存先を使用します。</summary>
    public ClusterReplication(PeerCluster cluster, IDurableRepository repository, int maximumBytes = 4194304, TimeProvider? timeProvider = null)
    {
        this.cluster = cluster;
        this.repository = repository;
        this.maximumBytes = maximumBytes;
        preparationCapabilities = new(cluster);
        journal = new(repository);
        recovery = new(cluster, repository, journal, gate, timeProvider ?? TimeProvider.System);
    }

    /// <summary>Peerの保存受付と、圧縮対応の参照専用照会を一度登録します。</summary>
    /// <returns>同じPeerが所有する複製処理です。</returns>
    internal static ClusterReplication Create(PeerCluster cluster, IDurableRepository repository, int maximumBytes, TimeProvider? timeProvider = null)
    {
        cluster.RegisterInspection(ReplicationPreparationPayload.Capability, (_, _) => Task.FromResult(new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(maximumBytes))));
        return new(cluster, repository, maximumBytes, timeProvider);
    }

    /// <summary>継続して未確認の保存準備がある場合の理由です。</summary>
    internal string? RecoveryProblem => recovery.Problem;

    /// <summary>通常複製の合間に、残留した元操作を一件ずつ確認します。</summary>
    /// <returns>有限な復旧確認です。</returns>
    internal Task RecoverAsync(CancellationToken cancellationToken = default) => recovery.RefreshAsync(cancellationToken);

    /// <summary>全固定対象のprepareとコミットを確認します。</summary>
    /// <returns>確定または確認不能結果。</returns>
    public ValueTask<DurableReceipt> ReplicateAsync(Guid operationId, IReadOnlyList<DurableMutation> mutations, CancellationToken cancellationToken = default) => ReplicateCoreAsync(operationId, mutations, null, cancellationToken);

    /// <summary>上位操作開始時の固定確認集合を各保存段階へ引き継ぎます。</summary>
    /// <returns>全固定対象の永続確認です。</returns>
    public ValueTask<DurableReceipt> ReplicateFixedAsync(Guid operationId, IReadOnlyList<DurableMutation> mutations, IReadOnlyList<PeerInfo> members, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(members);
        return ReplicateCoreAsync(operationId, mutations, members, cancellationToken);
    }

    /// <summary>指定済み確認集合を現在集合へ縮めず共有保存します。</summary>
    /// <returns>全対象の確定または確認不能結果。</returns>
    private async ValueTask<DurableReceipt> ReplicateCoreAsync(Guid operationId, IReadOnlyList<DurableMutation> mutations, IReadOnlyList<PeerInfo>? members, CancellationToken cancellationToken = default)
    {
        string fingerprint = RepositoryCausality.Fingerprint(mutations);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DurableReceipt? known = await GetOperationAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (known is not null)
            {
                foreach (DurableMutation mutation in mutations)
                {
                    if (await repository.IsHistoryRetiredAsync(mutation.Scope, mutation.Key, cancellationToken).ConfigureAwait(false))
                    {
                        return new(operationId, fingerprint, EOutcome.Unknown, [], RepositoryHistoryGuard.ExpiredReason);
                    }
                }
                if (members is not null)
                {
                    DurableEntry? original = await repository.ReadAsync("cluster:replication-members", operationId.ToString("N"), cancellationToken).ConfigureAwait(false);
                    PeerInfo[] savedMembers = original?.Value?.Json.Deserialize<PeerInfo[]>() ?? [];
                    if (!savedMembers.Select(peer => (peer.PeerId, peer.InstanceId)).ToHashSet().SetEquals(members.Select(peer => (peer.PeerId, peer.InstanceId))))
                    {
                        return new DurableReceipt(operationId, fingerprint, EOutcome.Failed, [], "Replication.MembershipMismatch");
                    }
                }
                if (string.Equals(known.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    return known;
                }
                return new DurableReceipt(operationId, fingerprint, EOutcome.Failed, [], "Replication.OperationMismatch");
            }
            PeerInfo[] targets = members?.ToArray() ?? cluster.GetPeers().Where(peer => PeerStatusRules.CanParticipate(peer.Status)).ToArray();
            if (!targets.Any(peer => peer.InstanceId == cluster.LocalInstanceId) || !cluster.GetPeers().Any(peer => peer.InstanceId == cluster.LocalInstanceId && PeerStatusRules.CanParticipate(peer.Status)))
            {
                return new DurableReceipt(operationId, fingerprint, EOutcome.NotExecuted, [], "Replication.LocalNotReady");
            }
            DurableReceipt pending = new(operationId, fingerprint, EOutcome.Unknown, [], ReplicationJournal.Preparing);
            ReplicationStartResult started = await ReplicationStart.BeginAsync(repository, operationId, mutations,
                [new DurableMutation("cluster:replication-members", operationId.ToString("N"), null, NodeValue.FromObject(targets)),
                 new DurableMutation("cluster:operations", operationId.ToString("N"), null, NodeValue.FromObject(pending))], cancellationToken).ConfigureAwait(false);
            if (started.Initialized.Outcome != EOutcome.Succeeded)
            {
                return new DurableReceipt(operationId, fingerprint, EOutcome.Unknown, [], "Replication.OriginalMembershipUnconfirmed");
            }
            ReplicationRequest request = new(operationId, started.Mutations);
            Lazy<ReplicationPreparationPayload> payload = new(() => ReplicationPreparationPayload.Create(request, maximumBytes));
            Lazy<JsonElement> commitBody = new(() => JsonSerializer.SerializeToElement(new ReplicationRequest(request.Id, [])));
            foreach (string phase in new[] { "prepare", "commit" })
            {
                DurableReceipt? localResult = started.Prepared;
                string commit = "commit";
                if (string.Equals(phase, commit, StringComparison.Ordinal))
                {
                    pending = pending with { Reason = ReplicationJournal.CommitDecided };
                    localResult = await journal.CommitAsync(pending, cancellationToken).ConfigureAwait(false);
                    if (localResult.Outcome != EOutcome.Succeeded)
                    {
                        DurableReceipt? decision = await journal.ReadAsync(operationId, cancellationToken).ConfigureAwait(false);
                        if (string.Equals(decision?.Reason, ReplicationJournal.Preparing, StringComparison.Ordinal))
                        {
                            await journal.SaveAsync(pending with { Reason = ReplicationJournal.AbortDecided }, cancellationToken).ConfigureAwait(false);
                            _ = await AbortTargetsAsync(request, targets, cancellationToken).ConfigureAwait(false);
                        }
                        return ReplicationJournal.PublicResult(await GetOperationAsync(operationId, cancellationToken).ConfigureAwait(false))!;
                    }
                }
                DurableReceipt?[] results = await Task.WhenAll(targets.Select(target => ApplyTargetPhaseAsync(phase, request, payload, commitBody, target, localResult, cancellationToken))).ConfigureAwait(false);
                for (int index = 0; index < targets.Length; index++)
                {
                    PeerInfo target = targets[index];
                    DurableReceipt? result = results[index];
                    if (result?.Outcome != EOutcome.Succeeded || !cluster.GetPeers().Any(peer => peer.InstanceId == target.InstanceId && peer.Status is not (EPeerStatus.Unavailable or EPeerStatus.Detached or EPeerStatus.Conflict)))
                    {
                        DurableReceipt unknown = pending;
                        string preparation = "prepare";
                        if (string.Equals(phase, preparation, StringComparison.Ordinal))
                        {
                            unknown = pending with { Reason = ReplicationJournal.AbortDecided };
                            await journal.SaveAsync(unknown, cancellationToken).ConfigureAwait(false);
                            bool aborted = await AbortTargetsAsync(request, targets, cancellationToken).ConfigureAwait(false);
                            if (aborted && result?.Outcome == EOutcome.Failed)
                            {
                                unknown = pending with { Outcome = EOutcome.Failed, Reason = result.Reason };
                            }
                        }
                        await journal.SaveAsync(unknown, cancellationToken).ConfigureAwait(false);
                        string commitment = "commit";
                        if (string.Equals(phase, commitment, StringComparison.Ordinal))
                        {
                            // コミットを再送せず、元の全参加者に残る同じ内容の永続結果だけで確認を補います。
                            return await ConfirmAsync(operationId, targets, cancellationToken).ConfigureAwait(false) ?? unknown;
                        }
                        return ReplicationJournal.PublicResult(unknown)!;
                    }
                }
            }
            DurableReceipt confirmed = await repository.ReadReceiptAsync(operationId, cancellationToken).ConfigureAwait(false) ?? pending;
            await journal.SaveAsync(confirmed, cancellationToken).ConfigureAwait(false);
            return confirmed;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>同じ段階の独立した保存先を並行確認し、全準備の終了前にはコミットしません。</summary>
    /// <returns>固定した実体一つの保存確認です。</returns>
    private async Task<DurableReceipt?> ApplyTargetPhaseAsync(string phase, ReplicationRequest request, Lazy<ReplicationPreparationPayload> payload, Lazy<JsonElement> commitBody, PeerInfo target, DurableReceipt? localPreparation, CancellationToken cancellationToken = default)
    {
        string prepare = "prepare";
        if (target.InstanceId == cluster.LocalInstanceId)
        {
            if (localPreparation is not null)
            {
                return localPreparation;
            }
            return await ApplyPhaseAsync(phase, request, cancellationToken).ConfigureAwait(false);
        }
        JsonElement body = commitBody.Value;
        string deliveryKind = phase;
        if (string.Equals(phase, prepare, StringComparison.Ordinal))
        {
            body = payload.Value.Plain;
            if (payload.Value.Compressed is { } compressed && payload.Value.Length <= await preparationCapabilities.MaximumBytesAsync(target, cancellationToken).ConfigureAwait(false))
            {
                body = compressed;
                deliveryKind = ReplicationPreparationPayload.Kind;
            }
        }
        WireMessage? reply = await cluster.ExchangeAsync(deliveryKind, target.PeerId, target.InstanceId, body, cancellationToken).ConfigureAwait(false);
        if (reply is null && string.Equals(phase, prepare, StringComparison.Ordinal))
        {
            // 遅着した準備を再送せず、同じ実体にある同じ内容の準備記録だけでACK欠測を補います。
            WireMessage? inspected = await cluster.ExchangeAsync("prepare.query", target.PeerId, target.InstanceId,
                JsonSerializer.SerializeToElement(new ReplicationRequest(request.Id, [])), cancellationToken).ConfigureAwait(false);
            DurableReceipt? prepared = inspected?.Body.Deserialize<DurableReceipt>();
            if (prepared?.Outcome == EOutcome.Succeeded && prepared.OperationId == request.Id
                && string.Equals(prepared.Fingerprint, RepositoryCausality.Fingerprint(request.Mutations), StringComparison.Ordinal))
            {
                return prepared;
            }
        }
        return reply?.Body.Deserialize<DurableReceipt>();
    }

    /// <summary>保存操作は再実行せず結果だけ読みます。</summary>
    /// <returns>確定した共有操作の結果。</returns>
    public async ValueTask<DurableReceipt?> GetOperationAsync(Guid operationId, CancellationToken cancellationToken = default)
        => ReplicationJournal.PublicResult(await journal.ReadAsync(operationId, cancellationToken).ConfigureAwait(false));

    /// <summary>元の参加者全員の永続結果だけを照会し、変更を再実行しません。</summary>
    /// <returns>再確認できた共有結果。</returns>
    public async ValueTask<DurableReceipt?> ConfirmAsync(Guid operationId, IReadOnlyList<PeerInfo> originalMembers, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(originalMembers);
        DurableReceipt? known = await GetOperationAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (known is null || known.Outcome != EOutcome.Unknown || originalMembers.Count == 0)
        {
            return known;
        }
        DurableEntry? storedMembers = await repository.ReadAsync("cluster:replication-members", operationId.ToString("N"), cancellationToken).ConfigureAwait(false);
        PeerInfo[]? fixedMembers = storedMembers?.Value?.Json.Deserialize<PeerInfo[]>();
        if (!ReplicationRecoveryProbe.ValidMembers(fixedMembers) || fixedMembers!.Length == 0
            || !originalMembers.All(original => fixedMembers.Any(member => string.Equals(member.PeerId, original.PeerId, StringComparison.Ordinal)
                && string.Equals(member.HostId, original.HostId, StringComparison.Ordinal))))
        {
            return known;
        }
        DurableReceipt? confirmed = null;
        bool? succeeded = null;
        string abortedReason = "Persistence.Aborted";
        foreach (PeerInfo original in fixedMembers)
        {
            PeerInfo? current = cluster.GetPeers().FirstOrDefault(peer => string.Equals(peer.PeerId, original.PeerId, StringComparison.Ordinal)
                && string.Equals(peer.HostId, original.HostId, StringComparison.Ordinal) && PeerStatusRules.CanParticipate(peer.Status));
            if (current is null)
            {
                return known;
            }
            DurableReceipt? receipt;
            if (current.InstanceId == cluster.LocalInstanceId)
            {
                receipt = await repository.ReadReceiptAsync(operationId, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                WireMessage? reply = await cluster.ExchangeAsync("query", current.PeerId, current.InstanceId, JsonSerializer.SerializeToElement(new ReplicationRequest(operationId, [])), cancellationToken).ConfigureAwait(false);
                receipt = reply?.Body.Deserialize<DurableReceipt>();
            }
            bool success = receipt?.OperationId == operationId && receipt.Outcome == EOutcome.Succeeded && string.Equals(receipt.Fingerprint, known.Fingerprint, StringComparison.Ordinal);
            bool aborted = receipt?.OperationId == operationId && receipt.Outcome == EOutcome.Failed && string.Equals(receipt.Reason, abortedReason, StringComparison.Ordinal)
                && (string.IsNullOrEmpty(receipt.Fingerprint) || string.Equals(receipt.Fingerprint, known.Fingerprint, StringComparison.Ordinal));
            if ((!success && !aborted) || succeeded.HasValue && succeeded.Value != success
                || success && confirmed is not null && !string.Equals(ContractJson.Hash(confirmed.Entries), ContractJson.Hash(receipt!.Entries), StringComparison.Ordinal))
            {
                return known;
            }
            succeeded = success;
            confirmed = receipt;
        }
        if (confirmed is not null)
        {
            if (succeeded == false)
            {
                confirmed = new(operationId, known.Fingerprint, EOutcome.Failed, [], abortedReason);
            }
            await journal.SaveAsync(confirmed, cancellationToken).ConfigureAwait(false);
        }
        return confirmed ?? known;
    }

    /// <summary>内部保存段階を通常UDP上で処理します。</summary>
    /// <returns>永続保存後の確認本文。</returns>
    public async Task<JsonElement> ReceiveAsync(string kind, JsonElement body, CancellationToken cancellationToken = default)
    {
        if (string.Equals(kind, ReplicationRecovery.Kind, StringComparison.Ordinal))
        {
            return await recovery.ReceiveAsync(body.Deserialize<Guid>(), cancellationToken).ConfigureAwait(false);
        }
        ReplicationRequest request;
        if (string.Equals(kind, ReplicationPreparationPayload.Kind, StringComparison.Ordinal))
        {
            request = ReplicationPreparationPayload.Read(body, maximumBytes);
            kind = "prepare";
        }
        else
        {
            request = body.Deserialize<ReplicationRequest>() ?? throw new InvalidDataException("保存要求が不正です。");
        }
        string preparationQuery = "prepare.query";
        if (string.Equals(kind, preparationQuery, StringComparison.Ordinal))
        {
            string? fingerprint = await repository.ReadPreparationAsync(request.Id, cancellationToken).ConfigureAwait(false);
            DurableReceipt inspected = fingerprint is null ? new(request.Id, string.Empty, EOutcome.Unknown, [], "Persistence.PreparationMissing")
                                                           : new(request.Id, fingerprint, EOutcome.Succeeded, []);
            return JsonSerializer.SerializeToElement(inspected);
        }
        string query = "query";
        if (string.Equals(kind, query, StringComparison.Ordinal))
        {
            return JsonSerializer.SerializeToElement(await repository.ReadReceiptAsync(request.Id, cancellationToken).ConfigureAwait(false) ?? new DurableReceipt(request.Id, string.Empty, EOutcome.Unknown, [], "Replication.ReceiptMissing"));
        }
        DurableReceipt receipt = await ApplyPhaseAsync(kind, request, cancellationToken).ConfigureAwait(false);

        // 段階のACKは成否と内容識別だけで足り、保存本文は元操作のqueryで照合します。
        return JsonSerializer.SerializeToElement(receipt with { Entries = [] });
    }

    /// <summary>同一操作の予約と確定を実施します。</summary>
    /// <returns>保存結果。</returns>
    private ValueTask<DurableReceipt> ApplyPhaseAsync(string phase, ReplicationRequest request, CancellationToken cancellationToken = default)
    {
        string prepare = "prepare";
        string commit = "commit";
        string abort = "abort";
        if (string.Equals(phase, prepare, StringComparison.Ordinal))
        {
            return repository.PrepareAsync(request.Id, request.Mutations, cancellationToken);
        }
        if (string.Equals(phase, commit, StringComparison.Ordinal))
        {
            return repository.CommitAsync(request.Id, cancellationToken);
        }
        if (string.Equals(phase, abort, StringComparison.Ordinal))
        {
            return repository.AbortPreparedAsync(request.Id, cancellationToken);
        }
        return ValueTask.FromResult(new DurableReceipt(request.Id, string.Empty, EOutcome.NotExecuted, [], "Replication.UnknownPhase"));
    }

    /// <summary>コミット未送信の操作だけを全固定対象で中止します。</summary>
    /// <returns>中止要求の確認完了。</returns>
    private async Task<bool> AbortTargetsAsync(ReplicationRequest request, IReadOnlyList<PeerInfo> targets, CancellationToken cancellationToken = default)
    {
        bool confirmed = true;
        string aborted = "Persistence.Aborted";
        foreach (PeerInfo target in targets)
        {
            DurableReceipt? result;
            if (target.InstanceId == cluster.LocalInstanceId)
            {
                result = await repository.AbortPreparedAsync(request.Id, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                WireMessage? response = await cluster.ExchangeAsync("abort", target.PeerId, target.InstanceId, JsonSerializer.SerializeToElement(new ReplicationRequest(request.Id, [])), cancellationToken).ConfigureAwait(false);
                result = response?.Body.Deserialize<DurableReceipt>();
            }
            confirmed &= string.Equals(result?.Reason, aborted, StringComparison.Ordinal);
        }
        return confirmed;
    }

    /// <summary>操作直列化の資源を解放します。</summary>
    public void Dispose() => gate.Dispose();

}
