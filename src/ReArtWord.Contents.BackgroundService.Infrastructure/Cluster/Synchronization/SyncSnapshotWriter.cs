using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;

/// <summary>固定版を一件ずつ書き、差分再作成では選ばれた元バイト列だけをコピーします。</summary>
internal static class SyncSnapshotWriter
{
    /// <summary>先行状態と従来の固定書出しを同じwriterへ接続します。</summary>
    /// <returns>実際に転送する本文と索引です。</returns>
    internal static Task<(string[] Hashes, SyncSnapshotIndex Index)> WriteSourceAsync(Stream stream, IDurableRepository repository, SyncPreparedExport? prepared,
        RepositorySnapshot? supplied, bool inventory, SyncInventoryRequest? known, CancellationToken cancellationToken = default)
    {
        Task<(string[] Hashes, SyncSnapshotIndex Index)> Write(SnapshotExport source) => Task.FromResult(WriteSelected(stream, source, inventory, known, prepared?.Original.Hashes, cancellationToken));
        if (supplied is not null)
        {
            return Write(new(supplied, supplied.Receipts.Select(SnapshotReceipt.FromValue).ToArray()));
        }
        if (prepared is null)
        {
            return SnapshotExport.ReadAsync(repository, Write, cancellationToken);
        }
        return prepared.Source.ReadAsync(Write, cancellationToken);
    }

    /// <summary>保持境界の重なりと固定指紋を使い、必要な区分だけを一度書き出します。</summary>
    /// <returns>元の全区分指紋と実際に書いた本文の索引です。</returns>
    internal static (string[] Hashes, SyncSnapshotIndex Index) WriteSelected(Stream stream, SnapshotExport source, bool inventory, SyncInventoryRequest? known,
        IReadOnlyList<string>? fixedHashes, CancellationToken cancellationToken)
    {
        bool full = known is null || !SyncInventory.Overlaps(source.Snapshot.HistoryBoundary, known.Boundary);
        if (full && fixedHashes is null)
        {
            return WriteFull(stream, source, inventory, cancellationToken);
        }
        string[] hashes = fixedHashes?.ToArray() ?? (inventory ? SyncInventory.Describe(source)
                                                               : []);
        SyncSnapshotIndex index = Write(stream, source, hashes, full ? null
                                                                     : known!.Hashes, cancellationToken);
        return (hashes, index);
    }

    /// <summary>完全書出しでは一件を一度だけ復元し、その本文と指紋を同時に確定します。</summary>
    /// <returns>旧契約と同じ区分指紋と固定ファイルの索引です。</returns>
    internal static (string[] Hashes, SyncSnapshotIndex Index) WriteFull(Stream stream, SnapshotExport export, bool inventory, CancellationToken cancellationToken)
    {
        List<(Guid Id, string Hash)>? receipts = null;
        if (inventory)
        {
            receipts = [];
        }
        SyncSnapshotIndex index = Write(stream, export, [], null, cancellationToken, receipts);
        string[] hashes = [];
        if (receipts is not null)
        {
            hashes = SyncInventory.Describe(export.Snapshot, receipts);
        }
        return (hashes, index);
    }

    /// <summary>同じ読取り取引の本文だけを使用し、履歴全体のDTOを作りません。</summary>
    /// <returns>書き出したJSONの要素索引です。</returns>
    internal static SyncSnapshotIndex Write(Stream stream, SnapshotExport export, IReadOnlyList<string> hashes, IReadOnlyList<string>? known, CancellationToken cancellationToken,
        List<(Guid Id, string Hash)>? observed = null)
    {
        RepositorySnapshot snapshot = export.Snapshot;
        bool[] selected = Select(hashes, known);
        Dictionary<string, IReadOnlyList<SyncSnapshotRange>> sections = new(StringComparer.Ordinal);
        using Utf8JsonWriter writer = new(stream);
        writer.WriteStartObject();
        WriteArray(nameof(snapshot.Entries), snapshot.Entries, SyncInventory.EntryBucket, (output, value) => JsonSerializer.Serialize(output, value));
        WriteArray(nameof(snapshot.Receipts), export.Receipts, receipt => SyncInventory.ReceiptBucket(receipt.OperationId), (output, receipt) =>
        {
            DurableReceipt value = receipt.Read();
            observed?.Add((receipt.OperationId, receipt.Hash(value)));
            JsonSerializer.Serialize(output, value);
        });
        WriteArray(nameof(snapshot.OperationOwners), snapshot.OperationOwners, owner => SyncInventory.ReceiptBucket(owner.OperationId), (output, value) => JsonSerializer.Serialize(output, value));
        WriteArray(nameof(snapshot.CausalEntries), snapshot.CausalEntries, entry => SyncInventory.KeyBucket(entry.Scope, entry.Key), (output, value) => JsonSerializer.Serialize(output, value));
        WriteArray(nameof(snapshot.LegacyEntries), snapshot.LegacyEntries, entry => SyncInventory.KeyBucket(entry.Scope, entry.Key), (output, value) => JsonSerializer.Serialize(output, value));
        WriteProperty(writer, nameof(snapshot.HistoryBoundary), snapshot.HistoryBoundary);
        WriteArray(nameof(snapshot.RetiredOperations), snapshot.RetiredOperations, operation => SyncInventory.ReceiptBucket(operation.Id), (output, value) => JsonSerializer.Serialize(output, value));
        WriteProperty(writer, nameof(snapshot.ProtectedHistory), snapshot.ProtectedHistory);
        WriteArray(nameof(snapshot.RetiredKeys), snapshot.RetiredKeys, key => SyncInventory.KeyBucket(key.Scope, key.Key), (output, value) => JsonSerializer.Serialize(output, value));
        WriteProperty(writer, nameof(snapshot.PendingOperations), snapshot.PendingOperations);
        WriteProperty(writer, nameof(snapshot.DeferredHistory), snapshot.DeferredHistory);
        WriteProperty(writer, nameof(snapshot.DeferredSourcePeer), snapshot.DeferredSourcePeer);
        WriteProperty(writer, nameof(snapshot.RecoveredHistory), snapshot.RecoveredHistory);
        writer.WriteEndObject();
        writer.Flush();
        return new(SyncInventory.Metadata(snapshot), sections);

        void WriteArray<T>(string name, IEnumerable<T>? values, Func<T, int> bucket, Action<Utf8JsonWriter, T> serialize)
        {
            if (values is null)
            {
                return;
            }
            List<SyncSnapshotRange> ranges = [];
            writer.WriteStartArray(name);
            writer.Flush();
            foreach (T value in values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int partition = bucket(value);
                if (!selected[partition])
                {
                    continue;
                }
                long offset = stream.Position;
                if (ranges.Count != 0)
                {
                    offset++;
                }
                serialize(writer, value);
                writer.Flush();
                ranges.Add(new(partition, offset, stream.Position - offset));
            }
            writer.WriteEndArray();
            sections.Add(name, ranges);
        }
    }

    /// <summary>メタデータ以外を復号せず、同じ固定ファイルの変更区分だけを転送用へ写します。</summary>
    /// <returns>新しい固定ファイルに対応する索引です。</returns>
    internal static SyncSnapshotIndex Copy(Stream source, Stream destination, SyncSnapshotIndex index, IReadOnlyList<string> hashes, IReadOnlyList<string> known, CancellationToken cancellationToken)
    {
        bool[] selected = Select(hashes, known);
        Dictionary<string, IReadOnlyList<SyncSnapshotRange>> sections = new(StringComparer.Ordinal);
        JsonElement metadata = JsonSerializer.SerializeToElement(index.Metadata);
        using Utf8JsonWriter writer = new(destination);
        byte[] buffer = new byte[65536];
        writer.WriteStartObject();
        foreach (JsonProperty property in metadata.EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!index.Sections.TryGetValue(property.Name, out IReadOnlyList<SyncSnapshotRange>? ranges))
            {
                property.WriteTo(writer);
                continue;
            }
            List<SyncSnapshotRange> copied = [];
            writer.WriteStartArray(property.Name);
            writer.Flush();
            foreach (SyncSnapshotRange range in ranges)
            {
                if (!selected[range.Bucket])
                {
                    continue;
                }

                // 元のwriterが確定した要素だけを写し、JSONの途中片や入力由来の文字列を差し込みません。
                if (copied.Count != 0)
                {
                    destination.WriteByte((byte)',');
                }
                long offset = destination.Position;
                source.Position = range.Offset;
                long remaining = range.Length;
                while (0 < remaining)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int length = (int)Math.Min(buffer.Length, remaining);
                    source.ReadExactly(buffer.AsSpan(0, length));
                    destination.Write(buffer.AsSpan(0, length));
                    remaining -= length;
                }
                copied.Add(range with { Offset = offset });
            }
            writer.WriteEndArray();
            sections.Add(property.Name, copied);
        }
        writer.WriteEndObject();
        writer.Flush();
        return new(index.Metadata, sections);
    }

    /// <summary>既知区分がなければ完全転送とし、既知なら双方の指紋を検証します。</summary>
    /// <returns>転送する区分の選択です。</returns>
    private static bool[] Select(IReadOnlyList<string> hashes, IReadOnlyList<string>? known)
    {
        if (known is null)
        {
            return Enumerable.Repeat(true, SyncInventory.BucketCount).ToArray();
        }
        SyncInventory.Validate(hashes);
        SyncInventory.Validate(known);
        return Enumerable.Range(0, SyncInventory.BucketCount).Select(index => !string.Equals(hashes[index], known[index], StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    /// <summary>旧snapshotと同じnull省略規則で非区分の項目を書きます。</summary>
    private static void WriteProperty<T>(Utf8JsonWriter writer, string name, T value)
    {
        if (value is not null)
        {
            writer.WritePropertyName(name);
            JsonSerializer.Serialize(writer, value);
        }
    }
}
