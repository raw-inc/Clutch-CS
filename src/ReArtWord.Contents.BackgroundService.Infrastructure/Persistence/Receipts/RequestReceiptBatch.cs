namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

/// <summary>同時に届いた受付記録だけを束ね、耐久コミット前に受付成功を公開しません。</summary>
internal sealed class RequestReceiptBatch
{
    private readonly Lock gate = new();
    private Batch? waiting;

    /// <summary>呼出し元のいずれかが一回の保存を所有し、常駐workerや周期的なflushを作りません。</summary>
    /// <returns>この要求の比較保存結果です。</returns>
    internal async ValueTask<DurableEntry?> WriteAsync(ReceiptWrite write,
        Func<Func<IReadOnlyList<ReceiptWrite>>, CancellationToken, ValueTask<IReadOnlyList<DurableEntry?>>> apply, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequestReceiptPersistence.Validate(write.Scope, write.Key, write.Value);
        Pending item = new(write, cancellationToken);
        Batch batch;
        bool owner;
        using (gate.EnterScope())
        {
            owner = waiting is null || 256 <= waiting.Items.Count;
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

    /// <summary>一度だけ実行権を譲り、同時到着分を同じ取引へ渡します。意図した時間待ちは入れません。</summary>
    /// <returns>対象全員への、コミットまたは失敗の通知完了です。</returns>
    private async Task FlushAsync(Batch batch,
        Func<Func<IReadOnlyList<ReceiptWrite>>, CancellationToken, ValueTask<IReadOnlyList<DurableEntry?>>> apply, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        Pending[]? active = null;
        IReadOnlyList<ReceiptWrite> Take()
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
            return active.Select(item => item.Write).ToArray();
        }
        try
        {
            IReadOnlyList<DurableEntry?> results = await apply(Take, cancellationToken).ConfigureAwait(false);
            if (active is null || active.Length != results.Count)
            {
                throw new InvalidDataException("受付の一括保存結果が一致しません。");
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

    /// <summary>排他中だけ追記し、切り離した後は所有者だけが保存する一組です。</summary>
    private sealed record Batch
    {
        /// <summary>最大256件の同時受付です。</summary>
        internal List<Pending> Items
        {
            get;
        } = [];
    }

    /// <summary>待機中の呼出しだけが所有する記録と完了通知です。</summary>
    private sealed record Pending(ReceiptWrite Write, CancellationToken Cancellation)
    {
        /// <summary>保存処理中にノード本体の継続を実行しません。</summary>
        internal TaskCompletionSource<DurableEntry?> Completion
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
