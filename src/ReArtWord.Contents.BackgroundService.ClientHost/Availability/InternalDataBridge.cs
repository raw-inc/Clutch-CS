using System.Diagnostics.CodeAnalysis;
using ReArtWord.Contents.BackgroundService.ClientHost.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.Availability;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.InternalData;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Availability;

/// <summary>外部操作を扱わず、確認済み内部値と端末内候補を実体ごとに分離します。</summary>
// 抑制理由: セッションはBridgeの所有辞書に保持し、Host終了時に一括Disposeするため操作ごとに解放しません。
[SuppressMessage("SourceQuality", "RAW141", Justification = "所有辞書のsessionはHostの寿命まで保持し、Bridge.Disposeで解放します。") ]
internal sealed class InternalDataBridge : IDisposable
{
    private readonly IInternalDataRemote remote;
    private readonly string directory;
    private readonly IDurableRepository journal;
    private readonly Lock owners = new();
    private readonly Dictionary<(string Cluster, string Peer, Guid Instance), InternalDataSession> sessions = [];

    /// <summary>最大16実体、各1MiB/256項目の有限キャッシュを構成します。</summary>
    public InternalDataBridge(IInternalDataRemote remote, string stateDirectory)
    {
        ArgumentNullException.ThrowIfNull(remote);
        this.remote = remote;
        directory = Path.Combine(Path.GetFullPath(stateDirectory), "internal-data");
        Directory.CreateDirectory(directory);
        journal = new JsonFileRepository(Path.Combine(directory, "local-journal.json"));
    }

    /// <summary>届いた現在値と、オフライン利用可能な保存値を分けて取得します。</summary>
    /// <returns>実体・時点を保持した内部状態です。</returns>
    public async Task<InternalDataClientView> ReadAsync(string cluster, string peerId, InternalDataClientTarget request, CancellationToken cancellationToken = default)
    {
        Validate(request.Target);
        PeerInfo? peer = remote.Find(cluster, peerId);
        Guid instance = request.InstanceId ?? peer?.InstanceId ?? throw new InvalidOperationException("確認した実体IDが必要です。");
        InternalDataSession? session = Find(cluster, peerId, instance);
        string dataset = Dataset(request.Target);
        if (peer?.InstanceId == instance)
        {
            try
            {
                InternalDataReadResult value = await remote.ReadAsync(cluster, peer, request.Target, cancellationToken).ConfigureAwait(false);
                session ??= Create(cluster, peerId, instance, value.Policy);
                await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (session.Policy != value.Policy)
                    {
                        throw new InvalidOperationException("取得済みのoffline方針が変わりました。保持候補を確認してください。");
                    }
                    AvailabilityValue? previous = session.Availability.TryGetCurrent(EStoragePurpose.Runtime, dataset);
                    if (previous?.State.IsLocalUnconfirmed != true && value.ConfirmedKnown)
                    {
                        AvailabilityValue confirmed = await session.Availability.RememberConfirmedAsync(EStoragePurpose.Runtime, dataset, value.ConfirmedValue, value.ConfirmedRevision, true, cancellationToken).ConfigureAwait(false);
                        if (confirmed.State.CanRead)
                        {
                            session.Observations[dataset] = new(DateTimeOffset.UtcNow);
                        }
                        session.Prune();
                    }
                    AvailabilityValue current = value.Current;
                    AvailabilityValue? comparison = null;
                    if (previous?.State.IsLocalUnconfirmed == true)
                    {
                        current = previous;
                        comparison = value.Current;
                    }
                    return View(cluster, peerId, instance, request.Target, session, true, current, comparison);
                }
                finally
                {
                    session.Gate.Release();
                }
            }
            catch (IOException)
            {
                // 読取りの通信不能だけをオフラインへ移し、更新要求を作成しません。
            }
        }
        if (session is null)
        {
            throw new InvalidOperationException("この実体で確認済みの内部値がありません。");
        }
        AvailabilityValue offline = session.Availability.ReadOffline(EStoragePurpose.Runtime, dataset);
        if (peer is not null && peer.InstanceId != instance)
        {
            offline = offline with { State = offline.State with { CanWrite = false, Reason = "InternalData.InstanceChanged" } };
        }
        return View(cluster, peerId, instance, request.Target, session, false, offline);
    }

    /// <summary>内部値だけを端末内の未確定変更として保持します。</summary>
    /// <returns>未送信を明示する候補です。</returns>
    public async Task<InternalDataClientView> StageAsync(string cluster, string peer, InternalDataClientChange change, CancellationToken cancellationToken = default)
    {
        Validate(change.Target.Target);
        Guid instance = change.Target.InstanceId ?? throw new InvalidOperationException("元の実体が必要です。");
        InternalDataSession session = Require(cluster, peer, instance);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RejectNewInstance(cluster, peer, instance);
            AvailabilityWriteResult result = await session.Availability.StageOfflineAsync(EStoragePurpose.Runtime, Dataset(change.Target.Target), change.Value, change.ExpectedRevision, cancellationToken).ConfigureAwait(false);
            AvailabilityValue current = result.Current ?? session.Availability.ReadOffline(EStoragePurpose.Runtime, Dataset(change.Target.Target));
            if (result.Applied && session.Observations.TryGetValue(Dataset(change.Target.Target), out InternalDataObservation? previous))
            {
                session.Observations[Dataset(change.Target.Target)] = previous with { PendingCommandId = null, OperationId = null };
            }
            return View(cluster, peer, instance, change.Target.Target, session, false, current);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    /// <summary>人が指定した再接続時だけ、元の保存版を条件に強いCASを一回送信します。</summary>
    /// <returns>競合・不明を保持した統合結果です。</returns>
    public async Task<InternalDataClientView> ReconcileAsync(string cluster, string peerId, InternalDataClientTarget target, CancellationToken cancellationToken = default)
    {
        Validate(target.Target);
        Guid instance = target.InstanceId ?? throw new InvalidOperationException("元の実体が必要です。");
        InternalDataSession session = Require(cluster, peerId, instance);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PeerInfo peer = remote.Find(cluster, peerId) ?? throw new InvalidOperationException("元のPeerへ到達できません。");
            if (peer.InstanceId != instance)
            {
                throw new InvalidOperationException("実体変更後のPeerへ旧候補を自動適用しません。");
            }
            string dataset = Dataset(target.Target);
            AvailabilityValue? comparison = null;
            AvailabilityWriteResult result = await session.Availability.ReconcileAsync(EStoragePurpose.Runtime, dataset, async (expected, value, id, token) =>
            {
                InternalDataObservation observed = session.Observations.GetValueOrDefault(dataset) ?? new(null);
                session.Observations[dataset] = observed with { PendingCommandId = ClientCommandLedger.Identity(id, "internal-data.compare-exchange"), OperationId = id };
                InternalDataWriteResult written = await remote.WriteAsync(cluster, peer, new(id, target.Target, expected, value), true, token).ConfigureAwait(false);
                comparison = written.Result.Current;
                return written.Result;
            }, cancellationToken).ConfigureAwait(false);
            AvailabilityValue current = result.Current ?? session.Availability.ReadOffline(EStoragePurpose.Runtime, dataset);
            if (result.PersistenceOutcome == EOutcome.Succeeded && result.Applied)
            {
                session.Observations[dataset] = new(DateTimeOffset.UtcNow);
            }
            return View(cluster, peerId, instance, target.Target, session, true, current, comparison);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    /// <summary>元書込みが不明でない場合だけ、人の明示でローカル候補を破棄します。</summary>
    /// <returns>再確認した現在の保存値です。</returns>
    public async Task<InternalDataClientView> DiscardAsync(string cluster, string peerId, InternalDataClientTarget target, CancellationToken cancellationToken = default)
    {
        Validate(target.Target);
        Guid instance = target.InstanceId ?? throw new InvalidOperationException("元の実体が必要です。");
        InternalDataSession session = Require(cluster, peerId, instance);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string dataset = Dataset(target.Target);
            AvailabilityValue current = session.Availability.ReadOffline(EStoragePurpose.Runtime, dataset);
            string unknown = "Availability.OriginalWriteUnknown";
            if (string.Equals(current.State.Reason, unknown, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("元書込みの結果不明を破棄で隠しません。元要求を照会してください。");
            }
            PeerInfo peer = remote.Find(cluster, peerId) ?? throw new InvalidOperationException("現在の保存値を確認してください。");
            if (peer.InstanceId != instance)
            {
                throw new InvalidOperationException("別実体への切替は新しい対象として読み直してください。");
            }
            InternalDataReadResult value = await remote.ReadAsync(cluster, peer, target.Target, cancellationToken).ConfigureAwait(false);
            if (!value.ConfirmedKnown)
            {
                throw new InvalidOperationException("現在の確認済み保存値が不明です。");
            }
            AvailabilityValue confirmed = await session.Availability.RememberConfirmedAsync(EStoragePurpose.Runtime, dataset, value.ConfirmedValue, value.ConfirmedRevision, true, cancellationToken).ConfigureAwait(false);
            session.Observations[dataset] = new(DateTimeOffset.UtcNow);
            return View(cluster, peerId, instance, target.Target, session, true, confirmed);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    /// <summary>元操作記録の固定結果だけを取り込み、書込みを再実行しません。</summary>
    /// <returns>元の不明が解消した場合も候補の成否を区別した値です。</returns>
    public async Task<InternalDataClientView> QueryAsync(string cluster, string peerId, InternalDataClientTarget target, CancellationToken cancellationToken = default)
    {
        Validate(target.Target);
        Guid instance = target.InstanceId ?? throw new InvalidOperationException("元の実体が必要です。");
        InternalDataSession session = Require(cluster, peerId, instance);
        string dataset = Dataset(target.Target);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            InternalDataObservation observation = session.Observations.GetValueOrDefault(dataset) ?? throw new InvalidOperationException("元要求がありません。");
            if (observation.PendingCommandId is not Guid command || observation.OperationId is not Guid operation)
            {
                throw new InvalidOperationException("元要求がありません。");
            }
            InternalDataWriteResult? reply = await remote.QueryAsync(command, operation, cancellationToken).ConfigureAwait(false);
            if (reply is not null && reply.OperationId == operation)
            {
                AvailabilityWriteResult applied = await session.Availability.ResolveReconciliationAsync(EStoragePurpose.Runtime, dataset, operation, reply.Result, cancellationToken).ConfigureAwait(false);
                if (applied.PersistenceOutcome == EOutcome.Succeeded && applied.Applied)
                {
                    session.Observations[dataset] = observation with { ConfirmedAt = DateTimeOffset.UtcNow };
                }
            }
            AvailabilityValue value = session.Availability.TryGetCurrent(EStoragePurpose.Runtime, dataset) ?? session.Availability.ReadOffline(EStoragePurpose.Runtime, dataset);
            PeerInfo? peer = remote.Find(cluster, peerId);
            bool online = peer?.InstanceId == instance;
            if (!online)
            {
                value = session.Availability.ReadOffline(EStoragePurpose.Runtime, dataset);
            }
            if (peer is not null && peer.InstanceId != instance)
            {
                value = value with { State = value.State with { CanWrite = false, Reason = "InternalData.InstanceChanged" } };
            }
            return View(cluster, peerId, instance, target.Target, session, online, value);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    /// <summary>値の可否に加えて所有先と最終確認時刻を返します。</summary>
    /// <returns>明示した実体と値の状態です。</returns>
    private static InternalDataClientView View(string cluster, string peer, Guid instance, InternalDataTarget target, InternalDataSession session, bool online, AvailabilityValue value, AvailabilityValue? comparison = null)
    {
        InternalDataObservation? observation = session.Observations.GetValueOrDefault(Dataset(target));
        return new(cluster, peer, instance, target, observation?.ConfirmedAt, online, value, session.Policy, comparison, observation?.PendingCommandId, session.Availability.TryGetConfirmed(EStoragePurpose.Runtime, Dataset(target)));
    }

    /// <summary>明示対象以外の保存スコープを作りません。</summary>
    private static void Validate(InternalDataTarget target)
    {
        if (target?.IsValid() != true)
        {
            throw new ArgumentException("内部状態のWorkflow・scope・keyが不正です。", nameof(target));
        }
    }

    /// <summary>既存状態とは別の実体へ編集を付け替えません。</summary>
    private void RejectNewInstance(string cluster, string peer, Guid instance)
    {
        PeerInfo? current = remote.Find(cluster, peer);
        if (current is not null && current.InstanceId != instance)
        {
            throw new InvalidOperationException("実体が変更されています。");
        }
    }

    /// <summary>内部状態の対象を衝突しないキャッシュ用途へ写します。</summary>
    /// <returns>内部状態専用の識別子です。</returns>
    private static string Dataset(InternalDataTarget target) => "state:" + ContractJson.Hash(target);

    /// <summary>保持していない実体を暗黙生成しません。</summary>
    /// <returns>既存のキャッシュです。</returns>
    private InternalDataSession Require(string cluster, string peer, Guid instance) => Find(cluster, peer, instance) ?? throw new InvalidOperationException("確認済み基準を取得していません。");

    /// <summary>完全一致する所有実体のキャッシュだけを取得します。</summary>
    /// <returns>既存のキャッシュまたはnullです。</returns>
    private InternalDataSession? Find(string cluster, string peer, Guid instance)
    {
        using (owners.EnterScope())
        {
            return sessions.GetValueOrDefault((cluster, peer, instance));
        }
    }

    /// <summary>明示実体のキャッシュを有限件数で所有します。</summary>
    /// <returns>同じ方針を共有するキャッシュです。</returns>
    private InternalDataSession Create(string cluster, string peer, Guid instance, AvailabilityPolicy policy)
    {
        using (owners.EnterScope())
        {
            if (sessions.TryGetValue((cluster, peer, instance), out InternalDataSession? existing))
            {
                return existing;
            }
            if (16 <= sessions.Count)
            {
                throw new InvalidOperationException("内部cacheの実体数上限に達しました。未確定変更を確認してください。");
            }
            InternalDataSession created = new(peer, policy, journal, directory);
            sessions.Add((cluster, peer, instance), created);
            return created;
        }
    }

    /// <summary>Host内の未保存キャッシュの寿命を終了します。</summary>
    public void Dispose()
    {
        foreach (InternalDataSession session in sessions.Values)
        {
            session.Dispose();
        }
        sessions.Clear();
    }
}
