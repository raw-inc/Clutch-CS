using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Logging;

/// <summary>残存ログとローカル索引を対応させ、保持と転送確認を保存します。</summary>
internal sealed class LogStore : IDisposable
{
    private readonly IDurableRepository repository;
    private readonly LogArchive archive;
    private readonly string scope;
    private readonly SemaphoreSlim changes = new(1, 1);
    private readonly ConcurrentDictionary<Guid, LogSegment> segments = [];
    private readonly ConcurrentDictionary<Guid, byte> knownIds = [];
    private bool initialized;
    private bool rawInitialized;

    /// <summary>ログファイルと共有対象外の索引用途を設定します。</summary>
    internal LogStore(LogOptions options, IDurableRepository repository)
    {
        this.repository = repository;
        archive = new(options.Directory);
        scope = "peer:" + options.PeerId + ":log-index";
    }

    /// <summary>現在保持する不変segment一覧です。</summary>
    internal IReadOnlyList<LogSegment> Segments => segments.Values.ToArray();

    /// <summary>現在保持する重複除去済みログ数です。</summary>
    internal long EntryCount => knownIds.Count;

    /// <summary>永続索引を読み、元ファイルが残る未索引分を回復します。</summary>
    /// <returns>検索可能な状態の準備完了です。</returns>
    internal Task InitializeAsync(CancellationToken cancellationToken = default) => InitializeAsync(false, cancellationToken);

    /// <summary>非圧縮限定の初回検索で、未索引の圧縮本文まで復元しません。</summary>
    /// <returns>指定形式の準備完了です。後の通常初期化は残りも回復します。</returns>
    internal async Task InitializeAsync(bool uncompressedOnly, CancellationToken cancellationToken = default)
    {
        await changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (initialized || uncompressedOnly && rawInitialized)
            {
                return;
            }
            IReadOnlyList<DurableEntry> entries = await repository.ListAsync(scope, cancellationToken).ConfigureAwait(false);
            foreach (DurableEntry entry in entries.Where(entry => entry.Value is not null))
            {
                LogSegment segment = entry.Value!.Json.Deserialize<LogSegment>() ?? throw new IOException("ログindexを解析できません。");
                Add(segment);
            }
            foreach (LogSegment candidate in archive.Discover(uncompressedOnly))
            {
                if (segments.ContainsKey(candidate.Id))
                {
                    continue;
                }
                List<Guid> ids = [];
                long bytes = 0;
                await foreach (WorkflowLogEntry entry in archive.ReadAsync(candidate, uncompressedOnly, cancellationToken).ConfigureAwait(false))
                {
                    ids.Add(entry.Id);
                    bytes += JsonSerializer.SerializeToUtf8Bytes(entry).Length + 1;
                }
                if (uncompressedOnly && ids.Count == 0)
                {
                    continue;
                }
                LogSegment recovered = candidate with { EntryIds = ids, Bytes = bytes };
                await SaveAsync(recovered, cancellationToken).ConfigureAwait(false);
                Add(recovered);
            }
            rawInitialized = true;
            initialized = !uncompressedOnly;
        }
        finally
        {
            changes.Release();
        }
    }

    /// <summary>同じレベル・日付の一括処理をファイルと索引へ確定します。</summary>
    /// <returns>永続受付の完了です。</returns>
    internal async Task PersistAsync(IReadOnlyList<LogWorkItem> items, LogPolicy policy, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            HashSet<Guid> batchIds = [];
            List<(WorkflowLogEntry Entry, LogWorkItem Work)> fresh = [];
            foreach (LogWorkItem item in items)
            {
                WorkflowLogEntry entry = JsonSerializer.Deserialize<WorkflowLogEntry>(item.Payload.Span) ?? throw new IOException("queue内ログを解析できません。");
                if (!knownIds.ContainsKey(entry.Id) && batchIds.Add(entry.Id))
                {
                    fresh.Add((entry, item));
                }
            }
            foreach (IGrouping<(string Level, DateOnly Date, bool Forward), (WorkflowLogEntry Entry, LogWorkItem Work)> group in fresh.GroupBy(item => (item.Entry.Level, policy.Date(item.Entry.Time), item.Work.Forward)))
            {
                Guid[] ids = group.Select(item => item.Entry.Id).ToArray();
                byte[] identity = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(',', ids.Order())));
                Guid id = new(identity.AsSpan(0, 16));
                ReadOnlyMemory<byte>[] payloads = group.Select(item => item.Work.Payload).ToArray();
                LogSegment segment = new(id, group.Key.Level, group.Key.Date, ids, payloads.Sum(payload => (long)payload.Length + 1), false, group.Key.Forward, [],
                    FirstTime: group.Min(item => item.Entry.Time), LastTime: group.Max(item => item.Entry.Time));
                await archive.WriteAsync(segment, payloads, cancellationToken).ConfigureAwait(false);
                await SaveAsync(segment, cancellationToken).ConfigureAwait(false);
                Add(segment);
            }
        }
        finally
        {
            changes.Release();
        }
    }

    /// <summary>保持中の一segmentを逐次読取りします。</summary>
    /// <returns>ファイルに存在するログです。</returns>
    internal IAsyncEnumerable<WorkflowLogEntry> ReadAsync(LogSegment segment, CancellationToken cancellationToken = default)
        => ReadAsync(segment, false, cancellationToken);

    /// <summary>検索で指定した保存形式を維持し、圧縮中の形式変更も読取り境界へ伝えます。</summary>
    /// <returns>対象形式に存在するログです。</returns>
    internal async IAsyncEnumerable<WorkflowLogEntry> ReadAsync(LogSegment segment, bool uncompressedOnly, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        DateTimeOffset? first = null, last = null;
        await foreach (WorkflowLogEntry entry in archive.ReadAsync(segment, uncompressedOnly, cancellationToken).ConfigureAwait(false))
        {
            if (first is null || entry.Time < first)
            {
                first = entry.Time;
            }
            if (last is null || last < entry.Time)
            {
                last = entry.Time;
            }
            yield return entry;
        }
        if (first is not null && segments.TryGetValue(segment.Id, out LogSegment? current) && (current.FirstTime is null || current.LastTime is null))
        {
            segments.TryUpdate(segment.Id, current with { FirstTime = first, LastTime = last }, current);
        }
    }

    /// <summary>永続受付を確認した転送先だけを記録します。</summary>
    /// <returns>転送確認保存の完了です。</returns>
    internal async Task MarkForwardedAsync(Guid id, string peer, CancellationToken cancellationToken = default)
    {
        await changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (segments.TryGetValue(id, out LogSegment? segment))
            {
                LogSegment updated = segment with { ForwardedTo = segment.ForwardedTo.Append(peer).Distinct(StringComparer.Ordinal).ToArray() };
                await SaveAsync(updated, cancellationToken).ConfigureAwait(false);
                segments[id] = updated;
            }
        }
        finally
        {
            changes.Release();
        }
    }

    /// <summary>新規だけの短縮指定では既存の日数を保護します。</summary>
    /// <returns>既存保持方針への反映完了です。</returns>
    internal async Task ProtectExistingAsync(LogPolicy oldPolicy, LogPolicy next, LogRetentionChangeOptions choice, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (LogSegment segment in segments.Values)
            {
                LogRetentionRule previousRule = oldPolicy.Rule(segment.Level);
                LogRetentionRule nextRule = next.Rule(segment.Level);
                int? before = previousRule.TotalDays;
                int? after = nextRule.TotalDays;
                if (segment.ProtectTotalDays)
                {
                    if (before is null || segment.MinimumTotalDays is null)
                    {
                        before = null;
                    }
                    else
                    {
                        before = Math.Max(before.Value, segment.MinimumTotalDays.Value);
                    }
                }
                bool shortened = after is not null && (before is null || after < before);
                LogSegment updated = segment;
                if (shortened)
                {
                    updated = updated with { ProtectTotalDays = !choice.ApplyDeletionShorteningToExisting, MinimumTotalDays = before };
                }
                if (previousRule.RawDays != nextRule.RawDays || choice.ExpandExistingCompressedOnRawExtension)
                {
                    updated = updated with { ExpandOnRecovery = choice.ExpandExistingCompressedOnRawExtension && segment.Compressed };
                }
                if (updated == segment)
                {
                    continue;
                }
                await SaveAsync(updated, cancellationToken).ConfigureAwait(false);
                segments[updated.Id] = updated;
            }
        }
        finally
        {
            changes.Release();
        }
    }

    /// <summary>日付境界で未加工の→zstd→削除を進め、失敗時は元ファイルを維持します。</summary>
    /// <returns>一回の保持処理完了です。</returns>
    internal async Task MaintainAsync(LogPolicy policy, DateTimeOffset now, bool expandExisting, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool failed = false;
            DateOnly today = policy.Date(now);
            foreach (LogSegment original in segments.Values)
            {
                try
                {
                    LogSegment segment = original;
                    LogRetentionRule rule = policy.Rule(segment.Level);
                    int age = today.DayNumber - segment.Date.DayNumber;
                    if (!segment.Compressed && rule.RawDays <= age)
                    {
                        segment = await archive.CompressAsync(segment, cancellationToken).ConfigureAwait(false);
                        await SaveAsync(segment, cancellationToken).ConfigureAwait(false);
                        segments[segment.Id] = segment;
                        archive.DeleteOldForm(segment);
                    }
                    if (segment.Compressed && (expandExisting || segment.ExpandOnRecovery) && age < rule.RawDays)
                    {
                        segment = (await archive.ExpandAsync(segment, cancellationToken).ConfigureAwait(false)) with { ExpandOnRecovery = false };
                        await SaveAsync(segment, cancellationToken).ConfigureAwait(false);
                        segments[segment.Id] = segment;
                        archive.DeleteOldForm(segment);
                    }
                    int? total = rule.TotalDays;
                    if (segment.ProtectTotalDays)
                    {
                        if (segment.MinimumTotalDays is null || total is null)
                        {
                            total = null;
                        }
                        else
                        {
                            total = Math.Max(total.Value, segment.MinimumTotalDays.Value);
                        }
                    }
                    if (segment.Compressed && total is int days && days <= age)
                    {
                        archive.Delete(segment);
                        await DeleteIndexAsync(segment, cancellationToken).ConfigureAwait(false);
                        segments.TryRemove(segment.Id, out _);
                        foreach (Guid id in segment.EntryIds)
                        {
                            knownIds.TryRemove(id, out _);
                        }
                    }
                }
                catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or System.Data.Common.DbException or ZstdSharp.ZstdException)
                {
                    failed = true;
                }
            }
            if (failed)
            {
                throw new IOException("log.retention.incomplete");
            }
        }
        finally
        {
            changes.Release();
        }
    }

    /// <summary>メタデータと重複確認を検索へ公開します。</summary>
    private void Add(LogSegment segment)
    {
        segments[segment.Id] = segment;
        foreach (Guid id in segment.EntryIds)
        {
            knownIds[id] = 0;
        }
    }

    /// <summary>一segmentのローカルメタデータを永続保存します。</summary>
    /// <returns>保存完了です。</returns>
    private async Task SaveAsync(LogSegment segment, CancellationToken cancellationToken = default)
    {
        string key = segment.Id.ToString("N");
        EOutcome outcome = await repository.WriteLocalSnapshotAsync(scope, key, NodeValue.FromObject(segment), cancellationToken).ConfigureAwait(false);
        if (outcome != EOutcome.Succeeded)
        {
            throw new IOException("log.index.unavailable");
        }
    }

    /// <summary>期限削除をローカル索引へ反映します。</summary>
    /// <returns>索引削除完了です。</returns>
    private async Task DeleteIndexAsync(LogSegment segment, CancellationToken cancellationToken = default)
    {
        string key = segment.Id.ToString("N");
        DurableEntry? previous = await repository.ReadAsync(scope, key, cancellationToken).ConfigureAwait(false);
        DurableReceipt receipt = await repository.ApplyBatchAsync(Guid.CreateVersion7(), [new(scope, key, previous?.Version, null)], cancellationToken).ConfigureAwait(false);
        if (receipt.Outcome != EOutcome.Succeeded)
        {
            throw new IOException("log.index.delete.unavailable");
        }
    }

    /// <summary>変更処理が終了した後に同期資源を解放します。</summary>
    public void Dispose()
    {
        changes.Dispose();
    }
}
