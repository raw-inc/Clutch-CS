using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Events;

/// <summary>保存済み更新事象の発行を試み、再起動後も同じ通知から副作用を再開しません。</summary>
public sealed class UpdateEventPump(IDurableRepository repository, IWorkflowEvents events, string peerId)
{
    private readonly Dictionary<string, string> observedFingerprints = new(StringComparer.Ordinal);

    /// <summary>ClientHost全体更新の結果を、このPeerの発行待ちへ一度保存します。</summary>
    /// <returns>通知の保存結果です。更新本体は実行しません。</returns>
    public async Task<EOutcome> EnqueueAsync(Contracts.Updates.UpdateNotification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        string[] names = ["update.started", "update.peerChanged", "update.completed", "update.failed", "update.interrupted"];
        Contracts.Updates.UpdateNotification expected = Contracts.Updates.UpdateNotification.Create(notification.Name, notification.Data, notification.ObservedAt);
        if (!names.Contains(notification.Name, StringComparer.Ordinal) || notification.Data.OperationId == Guid.Empty || expected.Id != notification.Id)
        {
            return EOutcome.NotExecuted;
        }
        string scope = UpdateEventJournal.Scope(peerId);
        string key = notification.Id.ToString("N");
        if (await repository.ReadAsync(scope, key, cancellationToken).ConfigureAwait(false) is not null)
        {
            return EOutcome.Succeeded;
        }
        WorkflowEvent observed = new(notification.Id, notification.Name, notification.ObservedAt, peerId, ContractJson.ToValue(notification.Data), CausationId: notification.Data.OperationId);
        DurableReceipt receipt = await repository.ApplyBatchAsync(Guid.CreateVersion7(), [new(scope, key, null, ContractJson.ToValue(observed))], cancellationToken).ConfigureAwait(false);
        return receipt.Outcome;
    }

    /// <summary>最後に確認した発行待ち件数です。</summary>
    public int Pending
    {
        get; private set;
    }

    /// <summary>通知の受付記録を確認できない理由です。</summary>
    public string? Problem
    {
        get; private set;
    }

    /// <summary>購読開始後に保存通知を取り込みます。発行の再試行は行いません。</summary>
    /// <returns>最大128件の発行試行です。</returns>
    public async Task DrainAsync(CancellationToken cancellationToken = default)
    {
        string scope = UpdateEventJournal.Scope(peerId);
        try
        {
            await RecoverAsync(cancellationToken).ConfigureAwait(false);
            DurableEntry[] entries = (await repository.ListAsync(scope, cancellationToken).ConfigureAwait(false)).Where(entry => entry.Value is not null).ToArray();
            Pending = entries.Length;
            Problem = null;
            foreach ((DurableEntry Entry, WorkflowEvent Event) item in entries.Select(entry => (Entry: entry, Event: entry.Value!.Json.Deserialize<WorkflowEvent>(ContractJson.Options)
                ?? throw new InvalidDataException("更新事象が不正です。"))).OrderBy(item => item.Event.Time).Take(128))
            {
                DurableEntry entry = item.Entry;
                WorkflowEvent notification = item.Event;
                DurableReceipt receipt = await repository.ApplyBatchAsync(WorkflowStore.PhaseId(notification.Id, "hook-delivery"),
                    [new(scope, entry.Key, entry.Version, null)], cancellationToken).ConfigureAwait(false);
                if (receipt.Outcome != EOutcome.Succeeded)
                {
                    Problem = "UpdateEvents.DeliveryUnconfirmed";
                    return;
                }

                // 発行試行を先に記録します。クラッシュの隙間は欠落し得ますが、再起動で副作用を重複開始しません。
                await WorkflowEventBoundary.PublishAsync(events, notification, cancellationToken).ConfigureAwait(false);
                Pending--;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Data.Common.DbException or JsonException or InvalidOperationException)
        {
            Problem = "UpdateEvents.ReadUnconfirmed";
        }
    }

    /// <summary>状態保存後のクラッシュを補い、導入前の完了履歴を遡及通知しません。</summary>
    /// <returns>今回の機能で開始した更新だけの通知補完です。</returns>
    private async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        HashSet<string> owners = (await repository.ListAsync("peer:" + peerId + ":update-hook-owner", cancellationToken).ConfigureAwait(false))
            .Where(owner => owner.Value is not null).Select(owner => owner.Key).ToHashSet(StringComparer.Ordinal);
        IReadOnlyList<DurableObservation> observations = await repository.ObserveAsync("updates", observedFingerprints, owners, cancellationToken).ConfigureAwait(false);
        foreach (string removed in observedFingerprints.Keys.Except(observations.Select(item => item.Key), StringComparer.Ordinal).ToArray())
        {
            observedFingerprints.Remove(removed);
        }
        foreach (DurableObservation observation in observations)
        {
            if (observation.Entry?.Value is not { } value)
            {
                continue;
            }
            Contracts.Updates.Cluster.ClusterUpdateStatus status = value.Json.Deserialize<Contracts.Updates.Cluster.ClusterUpdateStatus>(ContractJson.Options) ?? throw new InvalidDataException("更新状態が不正です。");
            IReadOnlyList<DurableMutation> notifications = await UpdateEventJournal.CreateAsync(repository, peerId, null, status, cancellationToken).ConfigureAwait(false);
            if (notifications.Count != 0)
            {
                DurableReceipt receipt = await repository.ApplyBatchAsync(Guid.CreateVersion7(), notifications, cancellationToken).ConfigureAwait(false);
                if (receipt.Outcome != EOutcome.Succeeded)
                {
                    continue;
                }
            }
            observedFingerprints[observation.Key] = observation.Fingerprint;
        }
    }
}
