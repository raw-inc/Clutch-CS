using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Files.Transactions;

/// <summary>全パスの重複判定を直列化し、変更した予約だけを全参加者へ原子的に複製します。</summary>
internal sealed class FileReservationCoordinator(PeerCluster cluster, IDurableRepository repository) : IDisposable
{
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly FileReservationCapabilities capabilities = new(cluster);
    private bool closing;
    private FileReservationLedger? sharedLedger;
    private FileReservationLedger? localLedger;

    /// <summary>通常停止では新規受付を閉じ、保持中予約を含めて旧版も読める形式へ戻します。</summary>
    /// <returns>更新の旧版復帰に必要な台帳の準備です。</returns>
    internal async Task PrepareStopAsync(CancellationToken cancellationToken = default)
    {
        // LEGACY-FORMAT: file-reservation-list-v1 — 旧版への復帰サポート終了時に停止前の逆変換を撤去します。
        Volatile.Write(ref closing, true);
        await writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (bool local in new[] { true, false })
            {
                if (!local && (cluster.IsDetached || !string.Equals(cluster.CoordinatorId, cluster.LocalPeerId, StringComparison.Ordinal)))
                {
                    continue;
                }
                FileReservationLedger ledger = await ReadAsync(local, cancellationToken).ConfigureAwait(false);
                if (ledger.Slotted)
                {
                    Guid operation = Guid.CreateVersion7();
                    PeerInfo[] members = cluster.GetPeers().Where(peer => PeerStatusRules.CanParticipate(peer.Status)).ToArray();
                    DurableReceipt saved = await SaveAsync(operation, ledger.Changes(operation, null, null, false), members, local, cancellationToken).ConfigureAwait(false);
                    if (saved.Outcome != EOutcome.Succeeded)
                    {
                        throw new IOException("停止前の予約台帳の互換保存を確認できません。");
                    }
                }
            }
        }
        finally
        {
            writes.Release();
        }
    }

    /// <summary>待機中の競合確認は新しい永続履歴を生成しません。</summary>
    /// <returns>現在の予約可否です。取得そのものではありません。</returns>
    internal async Task<ClusterResponse> ProbeAsync(ClusterRequest command, CancellationToken cancellationToken = default)
    {
        FileLease lease = command.Payload.Json.Deserialize<FileLease>() ?? throw new InvalidDataException("予約範囲がありません。");
        if (!MayWrite(lease.LocalOnly, lease.Coordinator.InstanceId))
        {
            return new(EOutcome.NotExecuted, Reason: "File.CoordinatorChanged");
        }
        FileReservationLedger ledger = await ReadAsync(lease.LocalOnly, cancellationToken).ConfigureAwait(false);
        return Overlaps(ledger, lease) ? new(EOutcome.NotExecuted, Reason: "File.Overlap")
                                       : new(EOutcome.Succeeded);
    }

    /// <summary>同時に届いたパスの包含関係も、同じガード版へのCASで確認します。</summary>
    /// <returns>一つの取得要求に対応する確認結果です。</returns>
    internal async Task<ClusterResponse> AcquireAsync(ClusterRequest request, CancellationToken cancellationToken = default)
    {
        FileLease lease = request.Payload.Json.Deserialize<FileLease>() ?? throw new InvalidDataException("予約が不正です。");
        await writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!MayWrite(lease.LocalOnly, lease.Coordinator.InstanceId))
            {
                return new(EOutcome.NotExecuted, Reason: "File.CoordinatorChanged");
            }
            FileReservationLedger ledger = await ReadAsync(lease.LocalOnly, cancellationToken).ConfigureAwait(false);
            if (Overlaps(ledger, lease))
            {
                return Reply(new(EOutcome.NotExecuted, Reason: "File.Overlap"));
            }
            bool slots = await UseSlotsAsync(ledger, lease.LocalOnly, cancellationToken).ConfigureAwait(false);
            DurableReceipt saved = await SaveAsync(request.Id, ledger.Changes(request.Id, lease, null, slots), lease.Members, lease.LocalOnly, cancellationToken).ConfigureAwait(false);
            Remember(lease.LocalOnly, ledger.Updated(saved));
            return Reply(new(saved.Outcome, lease, saved.Reason));
        }
        finally
        {
            writes.Release();
        }
    }

    /// <summary>保存された所有情報と一致する予約だけを空きスロットへ戻します。</summary>
    /// <returns>元の実行やファイル内容を変更しない解放確認です。</returns>
    internal async Task<ClusterResponse> ReleaseAsync(ClusterRequest request, CancellationToken cancellationToken = default)
    {
        FileGateRelease release = request.Payload.Json.Deserialize<FileGateRelease>() ?? throw new InvalidDataException("解放が不正です。");
        await writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!MayWrite(release.Lease.LocalOnly, cluster.LocalInstanceId))
            {
                return new(EOutcome.NotExecuted, Reason: "File.CoordinatorChanged");
            }
            FileReservationLedger ledger = await ReadAsync(release.Lease.LocalOnly, cancellationToken).ConfigureAwait(false);
            if (ledger.Active.TryGetValue(release.Lease.Id, out FileLease? held)
                && !string.Equals(ContractJson.Hash(held), ContractJson.Hash(release.Lease), StringComparison.Ordinal))
            {
                return new(EOutcome.NotExecuted, Reason: "File.LeaseOwnerMismatch");
            }
            bool slots = await UseSlotsAsync(ledger, release.Lease.LocalOnly, cancellationToken).ConfigureAwait(false);
            DurableReceipt saved = await SaveAsync(request.Id, ledger.Changes(request.Id, null, release.Lease.Id, slots), release.Members, release.Lease.LocalOnly, cancellationToken).ConfigureAwait(false);
            Remember(release.Lease.LocalOnly, ledger.Updated(saved));
            return new(saved.Outcome, Reason: saved.Reason);
        }
        finally
        {
            writes.Release();
        }
    }

    /// <summary>旧版がCoordinatorとして再参加した場合は、保持中の予約ごと従来形式へ戻します。</summary>
    /// <returns>旧版が新形式を未予約として解釈しないための互換回復です。</returns>
    internal async Task RestoreLegacyCoordinatorAsync(CancellationToken cancellationToken = default)
    {
        // LEGACY-FORMAT: file-reservation-list-v1 — 旧Coordinatorとの混在サポート終了まで予約を引き継ぎます。
        PeerInfo[] members = cluster.GetPeers().Where(peer => PeerStatusRules.CanParticipate(peer.Status)).ToArray();
        PeerInfo? coordinator = members.FirstOrDefault(peer => string.Equals(peer.PeerId, cluster.CoordinatorId, StringComparison.Ordinal));
        if (cluster.IsDetached || coordinator is null || coordinator.InstanceId == cluster.LocalInstanceId
            || await capabilities.SupportsAsync(coordinator, cancellationToken).ConfigureAwait(false) != false)
        {
            return;
        }
        await writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            FileReservationLedger ledger = await ReadAsync(false, cancellationToken).ConfigureAwait(false);
            if (ledger.Slotted && string.Equals(cluster.CoordinatorId, coordinator.PeerId, StringComparison.Ordinal))
            {
                Guid operation = Guid.CreateVersion7();
                await SaveAsync(operation, ledger.Changes(operation, null, null, false), members, false, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            writes.Release();
        }
    }

    /// <summary>全参加者が対応済みなら、次のファイル実行を待たず旧一覧を原子的に置き換えます。</summary>
    /// <returns>現在の所有権・予約内容を維持した移行確認です。</returns>
    internal async Task MigrateAsync(bool local, CancellationToken cancellationToken = default)
    {
        if (!MayWrite(local, cluster.LocalInstanceId))
        {
            return;
        }
        await writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!MayWrite(local, cluster.LocalInstanceId))
            {
                return;
            }
            FileReservationLedger ledger = await ReadAsync(local, cancellationToken).ConfigureAwait(false);
            if (!ledger.HasLegacyFormat || !await UseSlotsAsync(ledger, local, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
            Guid operation = Guid.CreateVersion7();
            PeerInfo[] members = cluster.GetPeers().Where(peer => PeerStatusRules.CanParticipate(peer.Status)).ToArray();
            DurableReceipt saved = await SaveAsync(operation, ledger.Changes(operation, null, null, true), members, local, cancellationToken).ConfigureAwait(false);
            FileReservationLedger? migrated = ledger.Updated(saved);
            if (migrated?.Slotted != true || !string.Equals(ContractJson.Hash(ledger.Active), ContractJson.Hash(migrated.Active), StringComparison.Ordinal))
            {
                Remember(local, null);
                throw new IOException("予約台帳の新形式への移行を確認できません。");
            }
            Remember(local, migrated);
        }
        finally
        {
            writes.Release();
        }
    }

    /// <summary>未対応が確認されたPeerには旧形式を使い、未確認だけでは新形式を開始しません。</summary>
    /// <returns>今回の更新をスロットへ保存できるかです。</returns>
    private async Task<bool> UseSlotsAsync(FileReservationLedger ledger, bool local, CancellationToken cancellationToken = default)
    {
        if (local)
        {
            return true;
        }
        (bool all, bool legacy) = await capabilities.CheckAsync(cancellationToken).ConfigureAwait(false);
        return all || ledger.Slotted && !legacy;
    }

    /// <summary>どちらの形式でも競合候補を自動選択しません。</summary>
    /// <returns>比較更新する現在の台帳です。</returns>
    private async Task<FileReservationLedger> ReadAsync(bool local, CancellationToken cancellationToken = default)
    {
        string scope = FileReservationLedger.Scope(cluster.LocalPeerId, local);
        string slots = scope + ":slots";
        if ((await repository.GetConflictsAsync(scope, cancellationToken).ConfigureAwait(false)).Count != 0
            || (await repository.GetConflictsAsync(slots, cancellationToken).ConfigureAwait(false)).Count != 0)
        {
            throw new InvalidDataException("ファイル予約の保存状態が競合しています。");
        }
        FileReservationLedger? cached = local ? Volatile.Read(ref localLedger)
                                              : Volatile.Read(ref sharedLedger);
        if (cached is not null && await cached.MatchesAsync(repository, cancellationToken).ConfigureAwait(false))
        {
            return cached;
        }
        FileReservationLedger current = await FileReservationLedger.ReadAsync(repository, scope, cancellationToken).ConfigureAwait(false);
        Remember(local, current);
        return current;
    }

    /// <summary>独立運転と共有予約の現在値を混ぜず、解放後の本文を新しい集合へ置き換えます。</summary>
    private void Remember(bool local, FileReservationLedger? ledger)
    {
        if (local)
        {
            Volatile.Write(ref localLedger, ledger);
        }
        else
        {
            Volatile.Write(ref sharedLedger, ledger);
        }
    }

    /// <summary>離脱ローカル以外は固定した全参加者の確認を必要とします。</summary>
    /// <returns>ガードと予約変更の原子的な保存確認です。</returns>
    private ValueTask<DurableReceipt> SaveAsync(Guid id, IReadOnlyList<DurableMutation> changes, IReadOnlyList<PeerInfo> members, bool local, CancellationToken cancellationToken = default)
        => local ? repository.ApplyBatchAsync(id, changes, cancellationToken)
                 : cluster.ReplicateFixedAsync(id, changes, members, cancellationToken);

    /// <summary>通常のCoordinatorと独立運転の所有Peerだけに取得・解放を許します。</summary>
    /// <returns>この実体で調停できるかです。</returns>
    private bool MayWrite(bool local, Guid expected) => !Volatile.Read(ref closing) && cluster.LocalInstanceId == expected
        && (string.Equals(cluster.CoordinatorId, cluster.LocalPeerId, StringComparison.Ordinal)
            || local && cluster.GetPeers().Any(peer => peer.InstanceId == cluster.LocalInstanceId && peer.Status is EPeerStatus.Detached or EPeerStatus.Ready));

    /// <summary>複数範囲を一括で比較し、一部だけを予約しません。</summary>
    /// <returns>同じHostと包含パスに競合する予約があるかです。</returns>
    private static bool Overlaps(FileReservationLedger ledger, FileLease lease) => ledger.Active.ContainsKey(lease.Id)
        || ledger.Active.Values.Any(owner => owner.Footprints.Any(first => lease.Footprints.Any(second => FileOverlap.Conflicts(first, second))));

    /// <summary>受付結果を同じ既存RPC形式へ返します。</summary>
    /// <returns>予約確認応答です。</returns>
    private static ClusterResponse Reply(FileLeaseResult result) => new(result.Outcome, NodeValue.FromObject(result), result.Reason);

    /// <summary>全受付タスクの終了後に調停の排他を解放します。</summary>
    public void Dispose() => writes.Dispose();
}
