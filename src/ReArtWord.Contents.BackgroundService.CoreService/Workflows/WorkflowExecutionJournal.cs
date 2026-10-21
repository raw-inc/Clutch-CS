using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.History;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.History;

namespace ReArtWord.Contents.BackgroundService.CoreService.Workflows;

/// <summary>ノードごとの同期記録を重ねず、確認したWF結果を有限の診断履歴として後から保存します。</summary>
internal sealed class WorkflowExecutionJournal(IDurableRepository repository, string peerId, WorkflowEngine engine) : IDisposable
{
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly Lock gate = new();
    private readonly Dictionary<int, WorkflowHistoryRecord> records = [];
    private readonly HashSet<Guid> persisted = [];
    private readonly string scope = "peer:" + peerId + ":workflow-results";
    private readonly string detailsScope = "peer:" + peerId + ":workflow-result-details";
    private bool loaded;
    private long sequence;
    private string? problem;

    /// <summary>未保存の診断を業務処理の失敗と混同しない表示用状態です。</summary>
    internal string? Problem => Volatile.Read(ref problem);

    /// <summary>再起動後にも参照できる直近の確定診断です。</summary>
    /// <returns>最大256件の結果または明示した要約です。</returns>
    internal IReadOnlyList<ExecutionResultView> Recent()
    {
        using (gate.EnterScope())
        {
            return records.Values.OrderByDescending(item => item.Sequence).Select(item => item.View).ToArray();
        }
    }

    /// <summary>新しい結果がある場合だけ記録し、入力や送信の実行経路から保存待ちを外します。</summary>
    /// <returns>Core停止までの所有作業です。</returns>
    internal async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(500));
        do
        {
            await FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>正常停止時はSourceの終了後にも呼び、最後の確認結果を保存します。</summary>
    /// <returns>現在見えている結果の保存試行です。業務処理の再実行は行いません。</returns>
    internal async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!loaded)
            {
                await LoadAsync(cancellationToken).ConfigureAwait(false);
            }
            IReadOnlyList<ExecutionResultView> recent = engine.RecentViews();

            // 完了順で循環スロットから外れても、開始順の表示範囲に残る保存済みIDを再記録しません。
            persisted.IntersectWith(recent.Select(Identity));
            ExecutionResultView[] pending = recent.Take(256).Where(view => !persisted.Contains(Identity(view))).Reverse().ToArray();
            int batchSize = repository is ILocalSnapshotBatches ? 256
                                                                : 1;
            foreach (ExecutionResultView[] batch in pending.Chunk(batchSize))
            {
                WorkflowHistoryRecord[] next = batch.Select((view, index) => new WorkflowHistoryRecord(checked(sequence + index + 1), ExecutionReportPresentation.Bound(view, 65536))).ToArray();
                EOutcome result = await SaveAsync(next, cancellationToken).ConfigureAwait(false);
                if (result != EOutcome.Succeeded)
                {
                    Volatile.Write(ref problem, "Execution.HistoryPersistenceUnconfirmed");
                    break;
                }
                using (gate.EnterScope())
                {
                    foreach (WorkflowHistoryRecord record in next)
                    {
                        records[(int)(record.Sequence % 256)] = record;
                        persisted.Add(Identity(record.View));
                    }
                }
                sequence = next[^1].Sequence;
                Volatile.Write(ref problem, null);
            }
        }
        catch (Exception error) when (error is not (OutOfMemoryException or OperationCanceledException))
        {
            Volatile.Write(ref problem, "Execution.HistoryPersistenceUnconfirmed");
        }
        finally
        {
            writes.Release();
        }
    }

    /// <summary>旧契約のヘッダーと圧縮詳細を一緒に確定し、非対応媒体には従来形式を保存します。</summary>
    /// <returns>全件確定または明示した保存不能です。</returns>
    private ValueTask<EOutcome> SaveAsync(WorkflowHistoryRecord[] batch, CancellationToken cancellationToken = default)
    {
        if (repository is not ILocalSnapshotBatches atomic)
        {
            WorkflowHistoryRecord record = batch[0];
            return repository.WriteLocalSnapshotAsync(scope, (record.Sequence % 256).ToString(System.Globalization.CultureInfo.InvariantCulture), ContractJson.ToValue(record), cancellationToken);
        }
        List<LocalSnapshotWrite> writes = [];
        for (int start = 0; start < batch.Length;)
        {
            (EOutcome outcome, DateTimeOffset completed) = WorkflowHistoryCodec.State(batch[start].View);
            long second = completed.ToUnixTimeSeconds();
            int count = 1;
            while (outcome == EOutcome.Succeeded && start + count < batch.Length && count < WorkflowHistoryCodec.MaximumBlockRecords)
            {
                (EOutcome nextOutcome, DateTimeOffset nextCompleted) = WorkflowHistoryCodec.State(batch[start + count].View);
                if (nextOutcome != EOutcome.Succeeded || nextCompleted.ToUnixTimeSeconds() != second)
                {
                    break;
                }
                count++;
            }
            WorkflowHistoryBlock? block = null;
            if (1 < count)
            {
                block = WorkflowHistoryCodec.Pack(batch.AsSpan(start, count).ToArray());
            }
            if (block is not null)
            {
                WorkflowHistoryRecord last = batch[start + count - 1];
                string key = (last.Sequence % 256).ToString(System.Globalization.CultureInfo.InvariantCulture);
                writes.Add(new(scope, key, ContractJson.ToValue(WorkflowHistoryCodec.BlockHeader(last))));
                writes.Add(new(detailsScope, key, ContractJson.ToValue(block)));
            }
            else
            {
                for (int index = start; index < start + count; index++)
                {
                    WorkflowHistoryRecord record = batch[index];
                    WorkflowHistoryRecord encoded = WorkflowHistoryCodec.Encode(record);
                    string key = (record.Sequence % 256).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    writes.Add(new(scope, key, ContractJson.ToValue(encoded)));
                    writes.Add(new(detailsScope, key, ContractJson.ToValue(encoded.Payload)));
                }
            }
            start += count;
        }
        return atomic.WriteSnapshotsAsync(writes, cancellationToken);
    }

    /// <summary>このPeerの有限診断だけを復元し、実行や保持値の再開には使用しません。</summary>
    /// <returns>保存済みスロットの読取り完了です。</returns>
    private async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DurableEntry> saved = await repository.ListAsync(scope, cancellationToken).ConfigureAwait(false);
        List<WorkflowHistoryRecord> restored = [];
        List<(DurableEntry Entry, WorkflowHistoryRecord Record)> headers = [];
        foreach (DurableEntry entry in saved.Take(256))
        {
            WorkflowHistoryRecord? record = entry.Value?.Json.Deserialize<WorkflowHistoryRecord>(ContractJson.Options);
            if (record is { Sequence: > 0, View: not null } && Identity(record.View) != Guid.Empty)
            {
                headers.Add((entry, record));
            }
        }
        long latest = headers.Select(item => item.Record.Sequence).DefaultIfEmpty().Max();
        foreach ((DurableEntry entry, WorkflowHistoryRecord record) in headers)
        {
            if (record.Sequence <= latest - 256)
            {
                continue;
            }
            WorkflowHistoryRecord result = record;
            if (string.Equals(result.View.DetailReason, WorkflowHistoryCodec.CompressedReason, StringComparison.Ordinal))
            {
                DurableEntry? details = await repository.ReadAsync(detailsScope, entry.Key, cancellationToken).ConfigureAwait(false);
                result = result with { Payload = details?.Value?.Json.Deserialize<WorkflowHistoryPayload>(ContractJson.Options) };
            }
            if (string.Equals(result.View.DetailReason, WorkflowHistoryCodec.BlockReason, StringComparison.Ordinal))
            {
                string key = (result.Sequence % 256).ToString(System.Globalization.CultureInfo.InvariantCulture);
                DurableEntry? details = await repository.ReadAsync(detailsScope, key, cancellationToken).ConfigureAwait(false);
                WorkflowHistoryBlock payload = details?.Value?.Json.Deserialize<WorkflowHistoryBlock>(ContractJson.Options) ?? throw new InvalidDataException("実行履歴ブロックがありません。");
                ExecutionResultView[] views = WorkflowHistoryCodec.Unpack(payload);
                if (result.Sequence != payload.FirstSequence + payload.Count - 1 || !string.Equals(key, entry.Key, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("実行履歴ブロックの終端が一致しません。");
                }
                _ = WorkflowHistoryCodec.Restore(result, payload, views);
                restored.AddRange(views.Select((view, index) => new WorkflowHistoryRecord(payload.FirstSequence + index, view))
                    .Where(item => latest - 256 < item.Sequence));
            }
            else
            {
                restored.Add(WorkflowHistoryCodec.Decode(result));
            }
        }
        using (gate.EnterScope())
        {
            foreach (WorkflowHistoryRecord result in restored)
            {
                records[(int)(result.Sequence % 256)] = result;
                persisted.Add(Identity(result.View));
                sequence = Math.Max(sequence, result.Sequence);
            }
        }
        loaded = true;
        Volatile.Write(ref problem, null);
    }

    /// <summary>APIの現在結果と保存済み診断を同じ実行IDで照合します。</summary>
    /// <returns>元の一件の識別子です。</returns>
    internal static Guid Identity(ExecutionResultView view) => view.Report?.Id ?? view.Summary?.Id ?? Guid.Empty;

    /// <summary>workerと最終flushの終了後に同期資源を破棄します。</summary>
    public void Dispose() => writes.Dispose();

}
