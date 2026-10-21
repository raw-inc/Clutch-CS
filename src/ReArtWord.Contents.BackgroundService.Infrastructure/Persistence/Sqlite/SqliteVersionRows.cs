using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;

/// <summary>検証済みの未変更行だけを再利用し、既存の共有版JSONを小さい変更単位で組み立てます。</summary>
internal sealed class SqliteVersionRows
{
    private static long MaximumCachedBytes => 67108864;
    private readonly Dictionary<long, CachedRow> rows = [];

    /// <summary>毎回実バイト列を検査した行だけを使い、削除済み行と上限超過分を保持しません。</summary>
    /// <returns>今回の行集合だけを所有するキャッシュと既存形式の共有版です。</returns>
    internal static (SqliteVersionRows Rows, string Version) Read(SqliteSession session, IReadOnlyList<SqliteSharedFingerprint.Row> verified,
        SqliteVersionRows? previous, bool retained, bool retirementChanged)
    {
        SqliteVersionRows next = new();
        List<CachedRow> current = [];
        string entryKind = "entry";
        foreach (SqliteSharedFingerprint.Row row in verified.OrderBy(row => !string.Equals(row.Kind, entryKind, StringComparison.Ordinal)))
        {
            CachedRow? old = previous?.rows.GetValueOrDefault(row.Id);
            if (old?.Source != row)
            {
                old = null;
            }
            DurableEntry entry = old?.Entry ?? session.Read<DurableEntry>(row.Kind, row.Key) ?? throw new InvalidDataException("検査済みの共有行がありません。");
            bool retired = false;
            if (retained && string.Equals(row.Kind, entryKind, StringComparison.Ordinal))
            {
                retired = old?.Retired ?? false;
                if (old is null || retirementChanged)
                {
                    retired = IsRetired(session, row.Scope, entry.Key);
                }
            }
            current.Add(new(row, entry, old?.Canonical, retired));
        }
        DurableEntry[] entries = current.Where(row => !row.Retired).Select(row => row.Entry).ToArray();
        string[] protectedHistory = [];
        if (retained)
        {
            protectedHistory = ProtectedHistory(session, entries);
        }
        Dictionary<DurableEntry, byte[]> canonical = [];
        DurableEntry[] ordered = RepositoryStateVersion.OrderedSharedEntries(RepositoryStateVersion.CurrentEntries(entries, retained, protectedHistory)).ToArray();
        HashSet<DurableEntry> selected = ordered.ToHashSet();
        long cachedBytes = 0;
        foreach (CachedRow row in current)
        {
            byte[]? encoded = row.Canonical;
            if (selected.Contains(row.Entry))
            {
                encoded ??= Encode(row.Entry);
                canonical.TryAdd(row.Entry, encoded);
            }
            long cost = row.Source.Length + (encoded?.LongLength ?? 0);
            if (cost <= MaximumCachedBytes - cachedBytes)
            {
                next.rows.Add(row.Source.Id, row with { Canonical = encoded });
                cachedBytes += cost;
            }
        }
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using HashBufferWriter buffer = new(hash);
        using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { MaxDepth = ContractJson.Options.MaxDepth }))
        {
            writer.WriteStartArray();
            foreach (DurableEntry entry in ordered)
            {
                // 同じcanonical writerで深さも検証した完全な一要素だけを再利用します。
                writer.WriteRawValue(canonical[entry], skipInputValidation: true);
            }
            writer.WriteEndArray();
        }
        return (next, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    /// <summary>保持開始後も必要な復旧基準と未完了操作を同じ規則で選びます。</summary>
    /// <returns>照合から除外しない履歴版の識別子です。</returns>
    internal static string[] ProtectedHistory(SqliteSession session, IEnumerable<DurableEntry> entries)
        => entries.Where(entry => entry.Value?.Json.ValueKind == JsonValueKind.Object
            && HistoryRetentionPolicy.Category("entry", entry.Scope) is { } category && RetainedHistoryEligibility.Protected(session, entry, category))
            .Select(entry => DurableRepository.GetAddress(entry.Scope, entry.Key) + entry.Version).ToArray();

    /// <summary>現在値の一覧と同じ明示失効索引だけを照合し、時刻から失効を推測しません。</summary>
    /// <returns>履歴本文を照合から除外するかです。</returns>
    private static bool IsRetired(SqliteSession session, string scope, string key)
    {
        using SqliteCommand command = session.HistoryCommand();
        command.CommandText = "SELECT 1 FROM retired_keys WHERE scope=$scope AND key=$key";
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() is not null;
    }

    /// <summary>配列の一段を残し、内容識別と同じ規則で変更された一行だけを正規化します。</summary>
    /// <returns>再利用できる完全なJSON要素です。</returns>
    private static byte[] Encode(DurableEntry entry)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { MaxDepth = ContractJson.Options.MaxDepth - 1 }))
        {
            ContractJson.WriteCanonical(writer, entry);
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>同じ保存行の検査値、復元済み本文、正規化済み表現を一緒に保持します。</summary>
    private sealed record CachedRow(SqliteSharedFingerprint.Row Source, DurableEntry Entry, byte[]? Canonical, bool Retired);
}
