using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Replication;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Replication;

/// <summary>残留した保存準備を巡回し、永続化済み判断だけで確定・中止します。</summary>
internal sealed class ReplicationRecovery(PeerCluster cluster, IDurableRepository repository, ReplicationJournal journal, SemaphoreSlim gate, TimeProvider clock)
{
    private Guid cursor;
    private DateTimeOffset? waitingSince;
    private DateTimeOffset retryAt;

    /// <summary>WFや通常要求とは別に扱う保存判断の照会です。</summary>
    internal static string Kind => "replication.recovery";

    /// <summary>通常の短い準備では表示せず、継続して確認できない元操作を診断します。</summary>
    internal string? Problem
    {
        get; private set;
    }

    /// <summary>実行中の複製を追い越さず、一巡につき元操作一件だけを回復します。</summary>
    /// <returns>今回の有限な保存準備確認です。</returns>
    internal async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (repository is not IPreparedOperations preparations || clock.GetUtcNow() < retryAt || !await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }
        bool ownsGate = true;
        try
        {
            IReadOnlyList<Guid> ids = await preparations.ReadRecoveryIdsAsync(cursor, 1, cancellationToken).ConfigureAwait(false);
            if (ids.Count == 0 && cursor != Guid.Empty)
            {
                cursor = Guid.Empty;
                ids = await preparations.ReadRecoveryIdsAsync(cursor, 1, cancellationToken).ConfigureAwait(false);
            }
            if (ids.Count == 0)
            {
                waitingSince = null;
                Problem = null;
                return;
            }
            cursor = ids[0];
            string? fingerprint = await repository.ReadPreparationAsync(cursor, cancellationToken).ConfigureAwait(false);
            fingerprint ??= (await journal.ReadAsync(cursor, cancellationToken).ConfigureAwait(false))?.Fingerprint;
            if (fingerprint is null)
            {
                return;
            }
            ReplicationRecoveryProbe local = await InspectAsync(cursor, true, cancellationToken).ConfigureAwait(false);
            gate.Release();
            ownsGate = false;
            PeerInfo? identity = cluster.GetPeers().FirstOrDefault(peer => peer.InstanceId == cluster.LocalInstanceId);
            using CancellationTokenSource expiry = new(TimeSpan.FromSeconds(5), clock);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(expiry.Token, cancellationToken);
            bool? decision = local.Decision(cursor, fingerprint, identity);
            if (decision is null)
            {
                foreach (PeerInfo peer in cluster.GetPeers().Where(peer => peer.InstanceId != cluster.LocalInstanceId && peer.Status is not (EPeerStatus.Unavailable or EPeerStatus.Detached)))
                {
                    WireMessage? response = await cluster.ExchangeAsync(Kind, peer.PeerId, peer.InstanceId, JsonSerializer.SerializeToElement(cursor), linked.Token).ConfigureAwait(false);
                    ReplicationRecoveryProbe? inspected = null;
                    if (response is not null)
                    {
                        try
                        {
                            inspected = ReplicationRecoveryProbe.Read(response.Body);
                        }
                        catch (Exception error) when (error is InvalidDataException or JsonException)
                        {
                            // 未対応の旧版や不完全な応答を根拠にせず、他参加者の保存結果を確認します。
                        }
                    }
                    decision = inspected is null ? null
                                                 : inspected.Decision(cursor, fingerprint, identity);
                    if (decision is not null)
                    {
                        break;
                    }
                }
            }
            if (decision is not null)
            {
                DurableReceipt? result = local.Stored;
                if (result?.Outcome != EOutcome.Succeeded && !(result?.Outcome == EOutcome.Failed && string.Equals(result.Reason, Aborted, StringComparison.Ordinal)))
                {
                    result = decision.Value ? await repository.CommitAsync(cursor, cancellationToken).ConfigureAwait(false)
                                            : await repository.AbortPreparedAsync(cursor, cancellationToken).ConfigureAwait(false);
                }
                if (result.Outcome == EOutcome.Succeeded || result.Outcome == EOutcome.Failed && string.Equals(result.Reason, Aborted, StringComparison.Ordinal))
                {
                    if (local.Operation is not null && local.Members.Count != 0
                        && (await cluster.ConfirmReplicationAsync(cursor, local.Members, linked.Token).ConfigureAwait(false))?.Outcome == EOutcome.Unknown)
                    {
                        Unconfirmed("Replication.RequiredAcknowledgementMissing");
                        return;
                    }
                    waitingSince = null;
                    Problem = null;
                    retryAt = clock.GetUtcNow();
                    return;
                }
            }
            Unconfirmed();
        }
        catch (Exception error) when (error is IOException or System.Data.Common.DbException or JsonException or InvalidOperationException
            || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            Unconfirmed();
        }
        finally
        {
            if (ownsGate)
            {
                gate.Release();
            }
        }
    }

    /// <summary>再起動後の未送信操作だけを中止へ進め、実行中の操作は照会だけに留めます。</summary>
    /// <returns>元IDの小さい確定判断と参加者情報です。</returns>
    internal async Task<JsonElement> ReceiveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        bool entered = await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonSerializer.SerializeToElement(await InspectAsync(id, entered, cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            if (entered)
            {
                gate.Release();
            }
        }
    }

    /// <summary>新形式でコミットを決定していない非実行中操作だけを、耐久的に中止へ変えます。</summary>
    /// <returns>再送時にも同じ判断となる照会結果です。</returns>
    private async Task<ReplicationRecoveryProbe> InspectAsync(Guid id, bool ownsGate, CancellationToken cancellationToken = default)
    {
        DurableReceipt? operation = await journal.ReadAsync(id, cancellationToken).ConfigureAwait(false);
        DurableEntry? entry = await repository.ReadAsync("cluster:replication-members", id.ToString("N"), cancellationToken).ConfigureAwait(false);
        PeerInfo[] members = entry?.Value?.Json.Deserialize<PeerInfo[]>() ?? [];
        if (!ReplicationRecoveryProbe.ValidMembers(members))
        {
            throw new InvalidDataException("保存準備の元参加者を確認できません。");
        }
        if (ownsGate && operation?.Outcome == EOutcome.Unknown && string.Equals(operation.Reason, ReplicationJournal.Preparing, StringComparison.Ordinal) && members.Length != 0)
        {
            operation = operation with { Reason = ReplicationJournal.AbortDecided };
            await journal.SaveAsync(operation, cancellationToken).ConfigureAwait(false);
        }
        DurableReceipt? stored = await repository.ReadReceiptAsync(id, cancellationToken).ConfigureAwait(false);
        if (stored is not null)
        {
            stored = stored with { Entries = [] };
        }
        if (operation is not null)
        {
            operation = operation with { Entries = [] };
        }
        return new(ReplicationRecoveryProbe.ProtocolName, operation, members, stored, !ownsGate);
    }

    /// <summary>未確認を長時間の無表示にせず、再照会の負荷も制限します。</summary>
    private void Unconfirmed(string? reason = null)
    {
        DateTimeOffset now = clock.GetUtcNow();
        waitingSince ??= now;
        retryAt = now.AddSeconds(1);
        if (TimeSpan.FromSeconds(5) <= now - waitingSince.Value)
        {
            Problem = reason ?? "Replication.PreparedDecisionUnconfirmed";
        }
    }

    /// <summary>元準備の取消が確定した理由です。</summary>
    private static string Aborted => "Persistence.Aborted";

}
