using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Retention;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Requests;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

/// <summary>JSON保存でも現在の復旧基準と未完了の一回性を保持します。</summary>
internal static class JsonHistoryEligibility
{
    /// <summary>確定した過去本文だけを期限対象にします。</summary>
    /// <returns>記録日時、保護対象ならnullです。</returns>
    internal static DateTimeOffset? Recorded(RepositoryDocument document, HistoryRow row, DateTimeOffset observed, DateTimeOffset now)
    {
        ERetentionCategory? category = HistoryRetentionPolicy.Category(row.Kind, row.Scope);
        if (category is null)
        {
            return null;
        }
        string receiptKind = "receipt";
        if (string.Equals(row.Kind, receiptKind, StringComparison.Ordinal))
        {
            DurableReceipt receipt = JsonSerializer.Deserialize<DurableReceipt>(row.Payload)!;
            if (receipt.Outcome == EOutcome.Unknown || !Enum.IsDefined(receipt.Outcome) || document.Prepared.ContainsKey(receipt.OperationId))
            {
                return null;
            }
            return observed;
        }
        DurableEntry entry = JsonSerializer.Deserialize<DurableEntry>(row.Payload)!;
        if (entry.Value?.Json.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array) || DurableRepository.IsReserved(document, new(entry.Scope, entry.Key, entry.Version, null))
            || document.Conflicts.Any(conflict => string.Equals(conflict.Scope, entry.Scope, StringComparison.Ordinal) && string.Equals(conflict.Key, entry.Key, StringComparison.Ordinal))
            || Protected(document, entry, category.Value))
        {
            return null;
        }
        if (category == ERetentionCategory.RequestHistory)
        {
            RequestReceipt? request = entry.Value.Json.Deserialize<RequestReceipt>();
            if (request?.Response is null || request.Response.Outcome == EOutcome.Unknown || now < request.ExpiresAt.AddMinutes(10))
            {
                return null;
            }
        }
        return HistoryRetentionPolicy.RecordedAt(entry.Value.Json, observed);
    }

    /// <summary>永久保存と最後の復旧基準を選別します。</summary>
    /// <returns>期間で整理しない行かです。</returns>
    internal static bool Protected(RepositoryDocument document, DurableEntry entry, ERetentionCategory category)
    {
        JsonElement value = entry.Value!.Json;
        string replication = "cluster:operations", members = "cluster:replication-members";
        if (string.Equals(entry.Scope, members, StringComparison.Ordinal))
        {
            string address = DurableRepository.GetAddress(replication, entry.Key);
            DurableEntry? result = document.Entries.GetValueOrDefault(address) ?? JsonHistoryAccess.Read<DurableEntry>(document, "entry", address);
            DurableReceipt? receipt = result?.Value?.Json.Deserialize<DurableReceipt>();
            if (receipt is null && Guid.TryParse(entry.Key, out Guid id))
            {
                receipt = JsonHistoryAccess.Receipt(document, id);
            }
            return receipt is null || receipt.Outcome == EOutcome.Unknown && !string.Equals(receipt.Reason, RepositoryHistoryGuard.ExpiredReason, StringComparison.Ordinal);
        }
        if (string.Equals(entry.Scope, replication, StringComparison.Ordinal))
        {
            return value.Deserialize<DurableReceipt>()?.Outcome == EOutcome.Unknown;
        }
        if (RetainedHistoryEligibility.StateInProgress(entry, category) is { } running)
        {
            return running;
        }
        if (category == ERetentionCategory.Snapshots)
        {
            SnapshotRecord snapshot = value.Deserialize<SnapshotRecord>()!;
            if (snapshot.Permanent)
            {
                return true;
            }
            SnapshotRecord? newest = document.Entries.Values.Where(candidate => string.Equals(candidate.Scope, entry.Scope, StringComparison.Ordinal) && candidate.Value is not null)
                .Select(candidate => candidate.Value!.Json.Deserialize<SnapshotRecord>()).Where(candidate => candidate is not null && candidate.Kind == snapshot.Kind
                    && string.Equals(candidate.TargetId, snapshot.TargetId, StringComparison.Ordinal))
                .OrderByDescending(candidate => candidate!.CapturedAt).ThenByDescending(candidate => candidate!.Id, StringComparer.Ordinal).FirstOrDefault();
            return string.Equals(newest?.Id, snapshot.Id, StringComparison.Ordinal);
        }
        if (category == ERetentionCategory.WorkflowVersions)
        {
            string prefix = "workflow.history:";
            if (entry.Scope.StartsWith(prefix, StringComparison.Ordinal))
            {
                string id = entry.Scope[prefix.Length..];
                return Current(document, "workflows", id, entry.Key) || Current(document, "workflow.activation", id, entry.Key);
            }
            return Current(document, "cluster.operational", "cluster-operational", entry.Key) || Current(document, "cluster.operational.activation", "cluster-operational", entry.Key);
        }
        string operations = "cluster:configuration-operations:", resultKey = "Result", routing = "peer:persistence-routing", ownerPrefix = "owner:";
        if (entry.Scope.StartsWith(operations, StringComparison.Ordinal))
        {
            return !HistoryJson.TryGet(value, resultKey, out JsonElement result) || result.ValueKind == JsonValueKind.Null || RetainedHistoryEligibility.ContainsUnknown(result);
        }
        if (string.Equals(entry.Scope, routing, StringComparison.Ordinal))
        {
            if (!entry.Key.StartsWith(ownerPrefix, StringComparison.Ordinal))
            {
                return true;
            }
            RoutingOwner owner = value.Deserialize<RoutingOwner>(Contracts.Serialization.ContractJson.Options)!;
            DurableReceipt? result = owner.Result ?? JsonHistoryAccess.Receipt(document, owner.Operation.OperationId);
            return result is null || result.Outcome == EOutcome.Unknown && !string.Equals(result.Reason, RepositoryHistoryGuard.ExpiredReason, StringComparison.Ordinal);
        }
        return false;
    }

    /// <summary>現在の保存版・反映版を本文から検証します。</summary>
    /// <returns>参照中の履歴版かです。</returns>
    private static bool Current(RepositoryDocument document, string scope, string key, string version)
    {
        DurableEntry? entry = document.Entries.GetValueOrDefault(DurableRepository.GetAddress(scope, key));
        return entry?.Value is not null && RetainedHistoryEligibility.HasProperty(entry.Value.Json, "Revision", version);
    }
}
