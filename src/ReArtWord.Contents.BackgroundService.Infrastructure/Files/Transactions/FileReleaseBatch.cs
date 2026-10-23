using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Files.Transactions;

/// <summary>調停待ちの解放をまとめ、全参加者の保存確認後に各要求へ結果を返します。</summary>
internal sealed class FileReleaseBatch
{
    private readonly Lock gate = new();
    private Batch? waiting;

    /// <summary>受付の一つが保存を所有し、常駐workerや時間待ちを追加しません。</summary>
    /// <returns>元の解放要求だけに対応する結果です。</returns>
    internal async Task<ClusterResponse> WriteAsync(Guid id, FileGateRelease release,
        Func<Func<IReadOnlyList<Request>>, CancellationToken, Task<IReadOnlyList<ClusterResponse>>> apply, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Pending item = new(new(id, release), cancellationToken);
        Batch batch;
        bool owner;
        using (gate.EnterScope())
        {
            owner = waiting is null || 64 <= waiting.Items.Count;
            if (owner)
            {
                waiting = new();
            }
            batch = waiting!;
            batch.Items.Add(item);
        }
        if (owner)
        {
            await FlushAsync(batch, apply, CancellationToken.None).ConfigureAwait(false);
        }
        return await item.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>調停権を取得するまでの同時到着分だけを引き取り、取消済み要求を除きます。</summary>
    /// <returns>全要求の結果通知です。</returns>
    private async Task FlushAsync(Batch batch,
        Func<Func<IReadOnlyList<Request>>, CancellationToken, Task<IReadOnlyList<ClusterResponse>>> apply, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        Pending[]? active = null;
        IReadOnlyList<Request> Take()
        {
            Pending[] owned;
            using (gate.EnterScope())
            {
                if (ReferenceEquals(waiting, batch))
                {
                    waiting = null;
                }
                owned = batch.Items.ToArray();
            }
            List<Pending> accepted = [];
            foreach (Pending item in owned)
            {
                if (item.Cancellation.IsCancellationRequested)
                {
                    item.Completion.TrySetCanceled(item.Cancellation);
                }
                else
                {
                    accepted.Add(item);
                }
            }
            active = accepted.ToArray();
            return active.Select(item => item.Request).ToArray();
        }
        try
        {
            IReadOnlyList<ClusterResponse> results = await apply(Take, cancellationToken).ConfigureAwait(false);
            if (active is null || active.Length != results.Count)
            {
                throw new InvalidDataException("予約解放の保存結果が一致しません。");
            }
            for (int index = 0; index < active.Length; index++)
            {
                active[index].Completion.TrySetResult(results[index]);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (active is null)
            {
                _ = Take();
            }
            foreach (Pending item in active!)
            {
                item.Completion.TrySetException(error);
            }
        }
    }

    /// <summary>元要求の識別子と、所有情報・固定参加者を保持します。</summary>
    internal sealed record Request(Guid Id, FileGateRelease Release);

    /// <summary>調停待ちに追加する最大64件の解放です。</summary>
    private sealed record Batch
    {
        /// <summary>引取り後は追記しません。</summary>
        internal List<Pending> Items
        {
            get;
        } = [];
    }

    /// <summary>受付ごとの取消と保存確認を独立して返します。</summary>
    private sealed record Pending(Request Request, CancellationToken Cancellation)
    {
        /// <summary>保存処理の中で呼出し元の後続処理を開始しません。</summary>
        internal TaskCompletionSource<ClusterResponse> Completion
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
