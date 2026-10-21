using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

/// <summary>削除済み結果の再実行と、古い同期による履歴の復活を防ぎます。</summary>
internal static class RepositoryHistoryGuard
{
    internal static string Scope => "peer:repository-history";
    internal static string ExpiredReason => "Persistence.HistoryExpired";

    /// <summary>全操作共通の削除境界を読みます。</summary>
    /// <returns>保持処理前ならnullです。</returns>
    internal static HistoryRetentionBoundary? Read(RepositoryDocument document)
        => document.Entries.GetValueOrDefault(DurableRepository.GetAddress(Scope, "boundary"))?.Value?.Json.Deserialize<HistoryRetentionBoundary>();

    /// <summary>既知の本文がない場合だけ、期限切れを未実行と混同せず返します。</summary>
    /// <returns>再実行を禁止する確認不能、または新規受付可能なnullです。</returns>
    internal static DurableReceipt? Expired(Guid id, HistoryRetentionBoundary? boundary, RetiredOperation? retired = null)
    {
        if (retired is not null || boundary is not null && OperationIdentity.TryGetIssuedAt(id, out DateTimeOffset issued) && issued < boundary.OperationBefore)
        {
            return new(id, retired?.Fingerprint ?? string.Empty, EOutcome.Unknown, [], ExpiredReason);
        }
        return null;
    }

    /// <summary>削除済みの操作開始記録を、過去の成功receiptだけで再作成させません。</summary>
    /// <returns>新規作成を拒否するキーかです。</returns>
    internal static bool IsRetiredKey(RepositoryDocument document, string scope, string key)
    {
        string address = DurableRepository.GetAddress(scope, key);
        if (document.History?.RetiredKeys.Contains(address) == true)
        {
            return true;
        }
        string kind = "entry";
        if (document.Entries.ContainsKey(address) || document.History?.Archives.ContainsKey(JsonHistoryAccess.Address(kind, address)) == true)
        {
            return false;
        }
        return IsSingleUse(scope) && TryOperationId(key, out Guid id) && Expired(id, Read(document)) is not null;
    }

    /// <summary>業務状態の任意キーを、操作IDの時刻で拒否しません。</summary>
    /// <returns>同じ受付を二度開始できない履歴の用途かです。</returns>
    internal static bool IsSingleUse(string scope) => HistoryRetentionPolicy.Category("entry", scope) is Contracts.Retention.ERetentionCategory.RequestHistory
        or Contracts.Retention.ERetentionCategory.OperationHistory or Contracts.Retention.ERetentionCategory.ExecutionHistory or Contracts.Retention.ERetentionCategory.Snapshots;

    /// <summary>保存所有先の接頭辞も除き、元操作の時刻を使用します。</summary>
    /// <returns>操作IDの表記を持つキーかです。</returns>
    internal static bool TryOperationId(string key, out Guid id)
    {
        string ownerPrefix = "owner:";
        if (key.StartsWith(ownerPrefix, StringComparison.Ordinal))
        {
            key = key[ownerPrefix.Length..];
        }
        return Guid.TryParse(key, out id);
    }

    /// <summary>時刻だけで捨てられない旧形式の共有受付を取り出します。</summary>
    /// <returns>ローカル操作を含まないキーです。</returns>
    internal static RetiredHistoryKey[] ExportKeys(RepositoryDocument document)
        => (document.History?.RetiredKeys.AsEnumerable() ?? []).Select(address => JsonSerializer.Deserialize<string[]>(address)!)
            .Where(parts => DurableRepository.IsShared(parts[0])).Select(parts => new RetiredHistoryKey(parts[0], parts[1])).ToArray();

    /// <summary>圧縮・削除の実績は過去へ戻さず、同期元の境界と合成します。</summary>
    internal static void Merge(RepositoryDocument document, HistoryRetentionBoundary incoming)
    {
        document.History ??= new();
        ArgumentNullException.ThrowIfNull(incoming.RawBefore);
        ArgumentNullException.ThrowIfNull(incoming.DeletedBefore);
        if (incoming.RawBefore.Keys.Concat(incoming.DeletedBefore.Keys).Any(category => !Enum.IsDefined(category)))
        {
            throw new InvalidDataException("保持境界の種類が不正です。");
        }
        HistoryRetentionBoundary? old = Read(document);
        Dictionary<Contracts.Retention.ERetentionCategory, DateTimeOffset> raw = new(old?.RawBefore ?? new Dictionary<Contracts.Retention.ERetentionCategory, DateTimeOffset>());
        Dictionary<Contracts.Retention.ERetentionCategory, DateTimeOffset> deleted = new(old?.DeletedBefore ?? new Dictionary<Contracts.Retention.ERetentionCategory, DateTimeOffset>());
        foreach ((Contracts.Retention.ERetentionCategory category, DateTimeOffset date) in incoming.RawBefore)
        {
            raw[category] = Max(raw.GetValueOrDefault(category), date);
        }
        foreach ((Contracts.Retention.ERetentionCategory category, DateTimeOffset date) in incoming.DeletedBefore)
        {
            deleted[category] = Max(deleted.GetValueOrDefault(category), date);
        }
        HistoryRetentionBoundary merged = new(raw, deleted, Max(old?.OperationBefore ?? default, incoming.OperationBefore), Max(old?.ObservedAt ?? default, incoming.ObservedAt));
        NodeValue value = NodeValue.FromObject(merged);
        document.Entries[DurableRepository.GetAddress(Scope, "boundary")] = new(Scope, "boundary", ContractJson.Hash(value), value);
    }

    /// <summary>スナップショットが実行可能な現在設定を消す条件には使用しません。</summary>
    /// <returns>過去の不変履歴が非圧縮区間より前かです。</returns>
    internal static bool OutsideWindow(DurableEntry entry, HistoryRetentionBoundary? boundary)
    {
        Contracts.Retention.ERetentionCategory? category = HistoryRetentionPolicy.Category("entry", entry.Scope);
        if (boundary is null || category is null || !DurableRepository.IsShared(entry.Scope) || entry.Value is null)
        {
            return false;
        }
        JsonElement value = entry.Value.Json;
        string permanent = "Permanent";
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(permanent, out JsonElement pin) && pin.ValueKind == JsonValueKind.True)
        {
            return false;
        }
        return HistoryRetentionPolicy.RecordedAt(value, DateTimeOffset.MaxValue) < boundary.RawBefore.GetValueOrDefault(category.Value);
    }

    /// <summary>時刻の単調な最大値を返します。</summary>
    /// <returns>後の境界です。</returns>
    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right)
    {
        if (left < right)
        {
            return right;
        }
        return left;
    }
}
