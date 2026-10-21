using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Scheduling;

/// <summary>元の管理実体による予定入力の取消を共有し、再開時にも維持します。</summary>
internal sealed class ScheduledInputCancellation(PeerCluster cluster, ScheduledInputStore store)
{
    /// <summary>一件の取消指示を、結果の保存や担当交代と比較更新します。</summary>
    /// <returns>取消指示、またはそれ以前の完了を確認できたか。</returns>
    internal async ValueTask<bool> CancelAsync(Guid id, CancellationToken cancellationToken = default)
    {
        foreach (bool local in new[] { false, true })
        {
            if (await store.ReadAsync(id, local, cancellationToken).ConfigureAwait(false) is not null)
            {
                return await MarkAsync(id, local, cancellationToken).ConfigureAwait(false);
            }
        }
        return false;
    }

    /// <summary>待受の受付が終了した後、そのSourceが予約した未完了入力を確認します。</summary>
    /// <returns>全対象について取消または完了を確認できたか。</returns>
    internal async ValueTask<bool> CancelSourceAsync(RemoteExecutionRequest source, CancellationToken cancellationToken = default)
    {
        if (source.TargetInstanceId != cluster.LocalInstanceId)
        {
            return false;
        }
        bool confirmed = true;
        foreach (bool local in new[] { false, true })
        {
            ScheduledInputIndex index = (await store.IndexAsync(local, cancellationToken).ConfigureAwait(false)).Index;
            foreach (ScheduledInputIndexEntry entry in index.Entries)
            {
                ScheduledInputRecord? record = (await store.ReadAsync(entry.Id, local, cancellationToken).ConfigureAwait(false))?.Record;
                if (record is null)
                {
                    confirmed = false;
                }
                else
                {
                    if (record.Source.Id == source.Id && record.Source.PeerInstanceId == source.TargetInstanceId)
                    {
                        confirmed &= await MarkAsync(entry.Id, local, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
        return confirmed;
    }

    /// <summary>別の管理実体が始めた処理を、元の実体の取消成功へ読み替えません。</summary>
    /// <returns>この実体で管理した一件の取消を記録できたか。</returns>
    private async ValueTask<bool> MarkAsync(Guid id, bool local, CancellationToken cancellationToken = default)
    {
        ScheduledInputRecord? saved = await store.ChangeAsync(id, local, current =>
        {
            if (current.Outcome is not null)
            {
                return current;
            }
            if (current.OwnerInstanceId != cluster.LocalInstanceId)
            {
                return null;
            }
            return current with { CancellationRequested = true };
        }, cancellationToken).ConfigureAwait(false);
        return saved is not null && (saved.Outcome is not null || saved.CancellationRequested);
    }
}
