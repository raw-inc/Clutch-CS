using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Resources;

/// <summary>ローカル診断履歴を有限のメモリへ保持し、定期採取の保存をまとめます。</summary>
internal sealed class ResourceHistory(IDurableRepository repository, string scope, ResourceOptions options) : IDisposable
{
    private readonly ResourceHistoryStore store = new(repository, scope);
    private readonly Queue<ResourceSample> samples = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool loaded;
    private DateTimeOffset? lastWrite;

    /// <summary>初回・明示採取・30秒経過時に保存し、その間は保存待ちと明示します。</summary>
    /// <returns>保存確認または保存待ちを区別した観測です。</returns>
    internal async Task<ResourceSample> AppendAsync(ResourceSample sample, bool persist, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LoadAsync(cancellationToken).ConfigureAwait(false);
            samples.Enqueue(sample with { HistoryPending = true });
            Trim(sample.ObservedAt);
            if (!persist && lastWrite is not null && sample.ObservedAt - lastWrite.Value < TimeSpan.FromSeconds(30))
            {
                return sample with { HistoryPending = true };
            }
            if (!await SaveAsync(cancellationToken).ConfigureAwait(false))
            {
                return sample;
            }
            return sample with { HistoryStored = true };
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>通常停止では保存待ちの最後の区間も保存します。</summary>
    /// <returns>保存待ちを排出する処理です。</returns>
    internal async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (samples.Any(sample => !sample.HistoryStored))
            {
                _ = await SaveAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>保存開始以後の失敗を保存待ちと偽らず、成功時だけ保存済みにします。</summary>
    /// <returns>原子的なスナップショットの保存成否です。</returns>
    private async Task<bool> SaveAsync(CancellationToken cancellationToken = default)
    {
        int count = samples.Count;
        ResourceSample[] snapshot = new ResourceSample[count];
        for (int index = 0; index < count; index++)
        {
            ResourceSample sample = samples.Dequeue();
            if (sample.HistoryPending)
            {
                sample = sample with { HistoryPending = false };
            }
            samples.Enqueue(sample);
            if (!sample.HistoryStored)
            {
                sample = sample with { HistoryStored = true };
            }
            snapshot[index] = sample;
        }
        if (!await store.WriteAsync(snapshot, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }
        samples.Clear();
        foreach (ResourceSample sample in snapshot)
        {
            samples.Enqueue(sample);
        }
        lastWrite = snapshot[^1].ObservedAt;
        return true;
    }

    /// <summary>初回だけ保存済み履歴を復元し、以後は保持中の実測を返します。</summary>
    /// <returns>書込みを起こさない現在の履歴です。</returns>
    internal async ValueTask<IReadOnlyList<ResourceSample>> ReadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LoadAsync(cancellationToken).ConfigureAwait(false);
            Trim(DateTimeOffset.UtcNow);
            return samples.ToArray();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>欠損・破損を空履歴として上書きしません。</summary>
    /// <returns>一度の履歴復元です。</returns>
    private async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (loaded)
        {
            return;
        }
        ResourceSample[] saved = await store.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (saved.Any(sample => sample is null))
        {
            throw new InvalidDataException("resource診断履歴に欠損したsampleがあります。");
        }
        foreach (ResourceSample sample in saved.TakeLast(options.HistoryCapacity))
        {
            samples.Enqueue(sample);
        }
        loaded = true;
    }

    /// <summary>件数と時刻の両方で古い観測を解放します。</summary>
    private void Trim(DateTimeOffset now)
    {
        DateTimeOffset cutoff = now.AddHours(-options.HistoryRetentionHours);
        while (samples.TryPeek(out ResourceSample? first) && (options.HistoryCapacity < samples.Count || first.ObservedAt < cutoff))
        {
            samples.Dequeue();
        }
    }

    /// <summary>利用終了後に履歴と同期資源を解放します。</summary>
    public void Dispose()
    {
        samples.Clear();
        gate.Dispose();
    }
}
