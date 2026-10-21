using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

/// <summary>現在値・未完了・共有同期の履歴を除き、元結果を完全保存できるローカル履歴だけを選びます。</summary>
internal static class HistoryEligibility
{
    /// <summary>日付不明の旧履歴は観測日を保持し、件数条件では元の格納順を使います。</summary>
    /// <returns>圧縮可能になる日時と参照用途、保護対象ならnullです。</returns>
    internal static Eligibility? Classify(SqliteSession session, HistoryRow row, long recordedAt, DateTimeOffset now)
    {
        SqlitePayload.Verify(row.Payload, row.Hash);
        long observed = recordedAt;
        if (observed == 0)
        {
            observed = now.ToUnixTimeSeconds();
        }
        string receipt = "receipt", entry = "entry", local = "local", prefix = "receipts:", routing = "peer:persistence-routing";
        if (string.Equals(row.Kind, receipt, StringComparison.Ordinal) && string.Equals(row.Scope, local, StringComparison.Ordinal))
        {
            DurableReceipt value = JsonSerializer.Deserialize<DurableReceipt>(row.Payload) ?? throw new InvalidDataException("保存履歴の形式が不正です。");
            return FinalLocal(value) ? new(observed, checked(observed + 600), value.Entries.Select(item => item.Scope).Distinct(StringComparer.Ordinal).ToArray())
                                     : null;
        }
        if (!string.Equals(row.Kind, entry, StringComparison.Ordinal))
        {
            return null;
        }
        DurableEntry saved = JsonSerializer.Deserialize<DurableEntry>(row.Payload) ?? throw new InvalidDataException("保存行の形式が不正です。");
        if (saved.Value is null || saved.Value.Kind != Contracts.Nodes.EValueKind.Json || saved.Value.Json.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        if (row.Scope?.StartsWith(prefix, StringComparison.Ordinal) == true)
        {
            JsonElement value = saved.Value.Json;
            string responseKey = "Response", outcomeKey = "Outcome", expiryKey = "ExpiresAt";
            if (!value.TryGetProperty(responseKey, out JsonElement response) || response.ValueKind != JsonValueKind.Object
                || !response.TryGetProperty(outcomeKey, out JsonElement outcome) || outcome.ValueKind != JsonValueKind.Number || !outcome.TryGetInt32(out int code)
                || code == (int)EOutcome.Unknown || !Enum.IsDefined((EOutcome)code)
                || !value.TryGetProperty(expiryKey, out JsonElement expiry) || expiry.ValueKind != JsonValueKind.String || !expiry.TryGetDateTimeOffset(out DateTimeOffset expires))
            {
                return null;
            }
            if (recordedAt == 0)
            {
                observed = Math.Min(observed, expires.ToUnixTimeSeconds());
            }
            return new(observed, checked(expires.ToUnixTimeSeconds() + 600), [row.Scope]);
        }
        string ownerPrefix = "owner:";
        if (!string.Equals(row.Scope, routing, StringComparison.Ordinal) || !saved.Key.StartsWith(ownerPrefix, StringComparison.Ordinal))
        {
            return null;
        }
        RoutingOwner owner = saved.Value.Json.Deserialize<RoutingOwner>(ContractJson.Options) ?? throw new InvalidDataException("操作所有先の形式が不正です。");
        if (owner.Operation.Scopes.Count == 0 || owner.Operation.Scopes.Any(DurableRepository.IsShared))
        {
            return null;
        }
        DurableReceipt? completed = owner.Result ?? session.Read<DurableReceipt>("receipt", owner.Operation.OperationId.ToString("N"));
        if (completed is null || !FinalLocal(completed))
        {
            return null;
        }
        return new(observed, checked(observed + 600), owner.Operation.Scopes);
    }

    /// <summary>未確定と共有・混在操作を除外します。</summary>
    /// <returns>元結果が確定したローカル操作かです。</returns>
    private static bool FinalLocal(DurableReceipt receipt) => receipt.Outcome != EOutcome.Unknown && receipt.Entries.Count != 0
        && receipt.Entries.All(entry => !DurableRepository.IsShared(entry.Scope));

    /// <summary>記録日、直近保護の期限、用途検索に必要な小さい索引です。</summary>
    internal sealed record Eligibility(long RecordedAt, long EligibleAfter, IReadOnlyList<string> Scopes);
}
