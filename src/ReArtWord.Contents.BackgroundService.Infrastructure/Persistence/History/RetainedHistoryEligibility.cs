using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Retention;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

/// <summary>現在の復旧基準・競合・未完了を守り、過去の確定履歴だけを選びます。</summary>
internal static class RetainedHistoryEligibility
{
    private static readonly string[] opaqueFields = ["Value", "Json", "Outputs", "Payload", "Definition"];

    /// <summary>新しい共通保持設定で扱える確定済みの行を分類します。</summary>
    /// <returns>保持の起点と直近保護です。現在値や未完了はnullです。</returns>
    internal static HistoryEligibility.Eligibility? Classify(SqliteSession session, HistoryRow row, long recordedAt, DateTimeOffset now)
    {
        SqlitePayload.Verify(row.Payload, row.Hash);
        ERetentionCategory? category = HistoryRetentionPolicy.Category(row.Kind, row.Scope);
        if (category is null || IsReserved(session, row))
        {
            return null;
        }
        DateTimeOffset observed = now;
        if (recordedAt != 0)
        {
            observed = DateTimeOffset.FromUnixTimeSeconds(recordedAt);
        }
        string receiptKind = "receipt";
        if (string.Equals(row.Kind, receiptKind, StringComparison.Ordinal))
        {
            DurableReceipt receipt = JsonSerializer.Deserialize<DurableReceipt>(row.Payload)!;
            if (receipt.Outcome == EOutcome.Unknown || !Enum.IsDefined(receipt.Outcome))
            {
                return null;
            }
            return new(observed.ToUnixTimeSeconds(), observed.AddMinutes(10).ToUnixTimeSeconds(), receipt.Entries.Select(entry => entry.Scope).Distinct(StringComparer.Ordinal).ToArray());
        }
        DurableEntry entry = JsonSerializer.Deserialize<DurableEntry>(row.Payload)!;
        if (entry.Value is null || entry.Value.Json.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
        {
            return null;
        }
        if (Protected(session, entry, category.Value))
        {
            return null;
        }
        DateTimeOffset date = HistoryRetentionPolicy.RecordedAt(entry.Value.Json, observed);
        DateTimeOffset eligible = date.AddMinutes(10);
        if (category == ERetentionCategory.RequestHistory)
        {
            HistoryEligibility.Eligibility? request = HistoryEligibility.Classify(session, row, recordedAt, now);
            return request;
        }
        return new(date.ToUnixTimeSeconds(), eligible.ToUnixTimeSeconds(), [entry.Scope]);
    }

    /// <summary>保存予約・競合のどちらかが残る行を本文保持から外しません。</summary>
    /// <returns>過去履歴とみなせない行かです。</returns>
    private static bool IsReserved(SqliteSession session, HistoryRow row)
    {
        using SqliteCommand command = session.HistoryCommand();
        command.CommandText = """
            SELECT 1 WHERE EXISTS(SELECT 1 FROM reservations WHERE address=$key OR operation=$key)
              OR EXISTS(SELECT 1 FROM records WHERE kind='prepared' AND key=$key)
              OR EXISTS(SELECT 1 FROM records WHERE kind='conflict' AND scope=$scope AND json_extract(payload,'$.Key')=json_extract($payload,'$.Key'))
            """;
        command.Parameters.AddWithValue("$key", row.Key);
        command.Parameters.AddWithValue("$scope", row.Scope ?? string.Empty);
        command.Parameters.AddWithValue("$payload", row.Payload);
        return command.ExecuteScalar() is not null;
    }

    /// <summary>履歴に見えても現在の復旧に必要な一件は保護します。</summary>
    /// <returns>自動保守から保護するかです。</returns>
    internal static bool Protected(SqliteSession session, DurableEntry entry, ERetentionCategory category)
    {
        JsonElement value = entry.Value!.Json;
        string replication = "cluster:operations", members = "cluster:replication-members";
        if (string.Equals(entry.Scope, members, StringComparison.Ordinal))
        {
            DurableEntry? result = session.Read<DurableEntry>("entry", DurableRepository.GetAddress(replication, entry.Key));
            DurableReceipt? receipt = result?.Value?.Json.Deserialize<DurableReceipt>();
            if (receipt is null && Guid.TryParse(entry.Key, out Guid id))
            {
                receipt = session.Read<DurableReceipt>("receipt", id.ToString("N"));
            }
            return receipt is null || receipt.Outcome == EOutcome.Unknown && !string.Equals(receipt.Reason, RepositoryHistoryGuard.ExpiredReason, StringComparison.Ordinal);
        }
        if (string.Equals(entry.Scope, replication, StringComparison.Ordinal))
        {
            return value.Deserialize<DurableReceipt>()?.Outcome == EOutcome.Unknown;
        }
        if (StateInProgress(entry, category) is { } running)
        {
            return running;
        }
        if (category == ERetentionCategory.Snapshots)
        {
            // 保護判定に不要なSnapshot本文やWorkflow定義を再構築しません。
            return value.TryGetProperty(nameof(SnapshotRecord.Permanent), out JsonElement permanent) && permanent.Deserialize<bool>()
                || session.IsLatestSnapshot(entry.Scope, entry.Key);
        }
        if (category == ERetentionCategory.WorkflowVersions)
        {
            string workflowPrefix = "workflow.history:";
            if (entry.Scope.StartsWith(workflowPrefix, StringComparison.Ordinal))
            {
                string id = entry.Scope[workflowPrefix.Length..];
                return session.IsCurrentRevision("workflows", id, entry.Key) || session.IsCurrentRevision("workflow.activation", id, entry.Key);
            }
            return session.IsCurrentRevision("cluster.operational", "cluster-operational", entry.Key) || session.IsCurrentRevision("cluster.operational.activation", "cluster-operational", entry.Key);
        }
        string operationPrefix = "cluster:configuration-operations:", resultKey = "Result", routing = "peer:persistence-routing", ownerPrefix = "owner:";
        if (entry.Scope.StartsWith(operationPrefix, StringComparison.Ordinal))
        {
            return !HistoryJson.TryGet(value, resultKey, out JsonElement result) || result.ValueKind == JsonValueKind.Null || ContainsUnknown(result);
        }
        if (string.Equals(entry.Scope, routing, StringComparison.Ordinal))
        {
            if (!entry.Key.StartsWith(ownerPrefix, StringComparison.Ordinal))
            {
                return true;
            }
            RoutingOwner? owner = value.Deserialize<RoutingOwner>(Contracts.Serialization.ContractJson.Options);
            DurableReceipt? receipt = owner?.Result ?? session.Read<DurableReceipt>("receipt", owner!.Operation.OperationId.ToString("N"));
            return receipt is null || receipt.Outcome == EOutcome.Unknown && !string.Equals(receipt.Reason, RepositoryHistoryGuard.ExpiredReason, StringComparison.Ordinal);
        }
        return false;
    }

    /// <summary>操作開始記録は現在状態とし、終端結果だけを履歴へ移します。</summary>
    /// <returns>種類固有の未完了判定、対象外ならnullです。</returns>
    internal static bool? StateInProgress(DurableEntry entry, ERetentionCategory category)
    {
        if (FileHistoryEligibility.IsScope(entry.Scope))
        {
            return !FileHistoryEligibility.Completed(entry);
        }
        string updates = "updates", completed = "Completed", statusName = "Status", outcome = "Outcome", running = "Running";
        JsonElement value = entry.Value!.Json;
        if (string.Equals(entry.Scope, updates, StringComparison.Ordinal))
        {
            return !HistoryJson.TryGet(value, completed, out JsonElement done) || done.ValueKind != JsonValueKind.True
                || !HistoryJson.TryGet(value, outcome, out JsonElement result) || !HistoryJson.Final(result) || ContainsUnknown(value);
        }
        if (category == ERetentionCategory.ExecutionHistory && HistoryJson.TryGet(value, statusName, out JsonElement status))
        {
            return !HistoryJson.TryGet(status, outcome, out JsonElement result) || !HistoryJson.Final(result)
                || HistoryJson.TryGet(status, running, out JsonElement active) && active.ValueKind == JsonValueKind.True;
        }
        return null;
    }

    /// <summary>複数段階のいずれかが未確認なら履歴を確定扱いしません。</summary>
    /// <returns>未確認の段階があるかです。</returns>
    internal static bool ContainsUnknown(JsonElement value)
    {
        string outcome = "Outcome";
        if (value.ValueKind == JsonValueKind.Object)
        {
            return value.EnumerateObject().Any(property => string.Equals(property.Name, outcome, StringComparison.OrdinalIgnoreCase)
                && HistoryJson.Unknown(property.Value) || !opaqueFields.Contains(property.Name, StringComparer.OrdinalIgnoreCase) && ContainsUnknown(property.Value));
        }
        return value.ValueKind == JsonValueKind.Array && value.EnumerateArray().Any(ContainsUnknown);
    }

    /// <summary>保存形式の包みを含め、対応する現在版だけを探します。</summary>
    /// <returns>一致する版があるかです。</returns>
    internal static bool HasProperty(JsonElement value, string name, string expected)
        => value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Any(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
            && property.Value.ValueKind == JsonValueKind.String && string.Equals(property.Value.GetString(), expected, StringComparison.Ordinal) || HasProperty(property.Value, name, expected));
}
