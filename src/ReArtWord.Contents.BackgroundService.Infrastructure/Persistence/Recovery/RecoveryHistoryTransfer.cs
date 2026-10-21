using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Causality;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Recovery;

/// <summary>過去の本文だけを有限単位で補完し、現在値や元の確定結果へ副作用を再適用しません。</summary>
internal static class RecoveryHistoryTransfer
{
    /// <summary>通常は32件・1MiBまで、一件が大きい場合も16MiBまでの元本文だけを返します。</summary>
    /// <returns>同じ存在証明に対応する本文または取得不能理由です。</returns>
    internal static IReadOnlyList<RecoveryRecordBody> Read(string path, IReadOnlyList<RecoveryRecord> receipts, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(receipts.Count, 32);
        using SqliteSession session = new(path, write: false);
        using SqliteCommand command = session.HistoryCommand();
        SqliteHistoryReader history = new(command.Connection!, command.Transaction!, true);
        List<RecoveryRecordBody> result = [];
        long total = 0;
        foreach (RecoveryRecord reference in receipts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecoveryRecordLedger.Validate(reference);
            string key = reference.Key;
            command.CommandText = "SELECT payload,hash FROM records WHERE kind=$kind AND key=$id";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", key);
            command.Parameters.AddWithValue("$kind", reference.Kind);
            string? payload = null, hash = null;
            using (SqliteDataReader reader = command.ExecuteReader())
            {
                if (reader.Read())
                {
                    payload = reader.GetString(0);
                    hash = reader.GetString(1);
                }
            }
            if (payload is null && history.Read(reference.Kind, key) is { } row)
            {
                payload = row.Payload;
                hash = row.Hash;
            }
            if (payload is null || !string.Equals(hash, reference.Hash, StringComparison.Ordinal))
            {
                string problem = "Sync.HistoryUnavailable";
                if (payload is not null)
                {
                    problem = "Sync.SnapshotChanged";
                }
                result.Add(new(reference, null, problem));
                continue;
            }
            int bytes = Encoding.UTF8.GetByteCount(payload);
            if (16777216 < bytes)
            {
                result.Add(new(reference, null, "Sync.HistoryRecordCapacity"));
                continue;
            }
            if (0 < result.Count && 1048576 < total + bytes)
            {
                break;
            }
            _ = ValidateBody(reference, payload);
            result.Add(new(reference, payload));
            total += bytes;
        }
        return result;
    }

    /// <summary>存在証明と本文の一致を検証し、現在版を巻き戻さず因果関係のある履歴を補完します。</summary>
    /// <returns>取込みまたは既存結果との照合が完了した件数です。</returns>
    internal static int Fill(string path, IReadOnlyList<RecoveryRecordBody> bodies, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bodies.Count, 32);
        if (16777216 < bodies.Sum(body => (long)Encoding.UTF8.GetByteCount(body.Payload ?? string.Empty)))
        {
            throw new InvalidDataException("背景履歴のバッチが上限を超えています。");
        }
        using SqliteSession session = new(path);
        Dictionary<(string Kind, string Key), object> validated = bodies.Where(body => body.Payload is not null)
            .ToDictionary(body => (body.Reference.Kind, body.Reference.Key), body => ValidateBody(body.Reference, body.Payload!));
        IReadOnlyDictionary<Guid, DurableReceipt> existing = SqliteReceipts.ReadStored(session, bodies.Where(body => body.Reference.Entry is null).Select(body => body.Reference.OperationId).ToArray(), cancellationToken);
        Dictionary<string, DurableEntry> entries = session.List<DurableEntry>("entry", keys: bodies.Where(body => body.Reference.Entry is not null).Select(body => body.Reference.Key).ToArray(), cancellationToken: cancellationToken)
            .ToDictionary(entry => DurableRepository.GetAddress(entry.Scope, entry.Key), StringComparer.Ordinal);
        RepositoryDocument ancestry = new() { ReadAncestors = (scope, key, versions) => session.ReadAncestors(scope, key, versions, cancellationToken) };
        DurableReceipt[] corrections = validated.Values.OfType<DurableReceipt>().Where(receipt => ReceiptReconciliation.CanReconcile(existing.GetValueOrDefault(receipt.OperationId), receipt)).ToArray();
        foreach (DurableReceipt receipt in corrections)
        {
            ancestry.Receipts[receipt.OperationId] = existing[receipt.OperationId];
        }
        SqliteCausality.Load(session, ancestry, bodies.Where(body => body.Reference.Entry is not null).Select(body => (body.Reference.Entry!.Scope, body.Reference.Entry.Key)));
        SqliteReceipts.LoadReconciliationEvidence(session, ancestry, corrections, cancellationToken);
        using SqliteCommand command = session.HistoryCommand();
        RepositoryDocument changes = ancestry;
        int completed = 0;
        List<RecoveryRecord> saved = [];
        List<RecoveryRecord> verified = [];
        foreach (RecoveryRecordBody body in bodies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecoveryRecord reference = body.Reference;
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", reference.Key);
            command.Parameters.AddWithValue("$kind", reference.Kind);
            command.CommandText = "SELECT payload,hash FROM recovery_history WHERE kind=$kind AND key=$id";
            RecoveryRecord? expected = null;
            using (SqliteDataReader reader = command.ExecuteReader())
            {
                if (reader.Read())
                {
                    string payload = reader.GetString(0);
                    SqlitePayload.Verify(payload, reader.GetString(1));
                    expected = JsonSerializer.Deserialize<RecoveryRecord>(payload);
                }
            }
            if (expected != reference)
            {
                // 先行同期で元の存在証明が更新された場合、古い背景応答では置き換えません。
                continue;
            }
            DurableReceipt? previous = existing.GetValueOrDefault(reference.OperationId);
            bool expired = reference.Entry is { } identity ? SqliteHistoryGuard.IsRetiredKey(session, identity.Scope, identity.Key)
                                                           : string.Equals(previous?.Reason, RepositoryHistoryGuard.ExpiredReason, StringComparison.Ordinal);
            if (!expired && (body.Problem is not null || body.Payload is null))
            {
                SetProblem(command, body.Problem ?? "Sync.HistoryUnavailable");
                continue;
            }
            if (!expired)
            {
                object value = validated[(reference.Kind, reference.Key)];
                if (value is DurableEntry entry)
                {
                    if (reference.Entry!.Causality is { } evidence)
                    {
                        RepositoryCausality.Import(ancestry, new([entry], [], CausalEntries: [evidence]));
                    }
                    DurableEntry? current = entries.GetValueOrDefault(reference.Key);
                    if (current is null)
                    {
                        changes.Entries[reference.Key] = entry;
                        saved.Add(reference);
                    }
                    else
                    {
                        if (!RepositoryMerge.IsAncestor(ancestry, entry, current))
                        {
                            // 未完了の保護は履歴削除を防ぐ規則であり、因果関係を確認した完了版の取込みは妨げません。
                            if (RepositoryMerge.IsAncestor(ancestry, current, entry))
                            {
                                changes.Entries[reference.Key] = entry;
                                saved.Add(reference);
                            }
                            else
                            {
                                SetProblem(command, "Sync.HistoryConflict");
                                continue;
                            }
                        }
                    }
                }
                else
                {
                    DurableReceipt receipt = (DurableReceipt)value;
                    bool missing = previous is null || string.Equals(previous.Reason, RecoveryRecordLedger.PendingReason, StringComparison.Ordinal);
                    if (!missing && !Equivalent(previous!, receipt))
                    {
                        if (!ReceiptReconciliation.TryReconcile(changes, receipt))
                        {
                            SetProblem(command, "Sync.HistoryConflict");
                            continue;
                        }
                        saved.Add(reference);
                    }
                    if (missing)
                    {
                        changes.Receipts[receipt.OperationId] = receipt;
                        saved.Add(reference);
                    }
                }
            }
            command.CommandText = "DELETE FROM recovery_history WHERE kind=$kind AND key=$id";
            command.ExecuteNonQuery();
            verified.Add(reference);
            completed++;
        }
        session.Save(changes);
        foreach (RecoveryRecord reference in saved)
        {
            string payload = bodies.Single(body => body.Reference == reference).Payload!;
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", reference.Key);
            command.Parameters.AddWithValue("$kind", reference.Kind);
            command.Parameters.AddWithValue("$payload", payload);
            command.Parameters.AddWithValue("$hash", reference.Hash);
            command.CommandText = "UPDATE records SET payload=$payload,hash=$hash WHERE kind=$kind AND key=$id AND hash<>$hash";
            command.ExecuteNonQuery();
            if (validated[(reference.Kind, reference.Key)] is DurableReceipt receipt)
            {
                SqliteReceiptFingerprint.Save(command.Connection!, command.Transaction!, receipt, reference.Hash);
            }
            command.CommandText = "UPDATE history_candidates SET recorded_at=$time WHERE kind=$kind AND key=$id";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", reference.Key);
            command.Parameters.AddWithValue("$kind", reference.Kind);
            command.Parameters.AddWithValue("$time", reference.RecordedAt);
            command.ExecuteNonQuery();
        }
        foreach (RecoveryRecord reference in verified)
        {
            RecoveryRecordLedger.RememberVerified(session, reference);
        }
        RecoveryRecordLedger.RefreshCompatibility(session);
        cancellationToken.ThrowIfCancellationRequested();
        session.Commit();
        return completed;
    }

    /// <summary>本文の識別と共有範囲を確認してから、元の結果を返します。</summary>
    /// <returns>検証済みの一件です。</returns>
    private static object ValidateBody(RecoveryRecord reference, string payload)
    {
        RecoveryRecordLedger.Validate(reference);
        if (16777216 < Encoding.UTF8.GetByteCount(payload))
        {
            throw new InvalidDataException("背景履歴の本文が上限を超えています。");
        }
        SqlitePayload.Verify(payload, reference.Hash);
        if (reference.Entry is { } identity)
        {
            DurableEntry entry = JsonSerializer.Deserialize<DurableEntry>(payload) ?? throw new InvalidDataException("背景の保存項目がありません。");
            if (!string.Equals(entry.Scope, identity.Scope, StringComparison.Ordinal) || !string.Equals(entry.Key, identity.Key, StringComparison.Ordinal)
                || !string.Equals(entry.Version, identity.Version, StringComparison.Ordinal))
            {
                throw new InvalidDataException("背景の保存項目と存在証明が一致しません。");
            }
            return entry;
        }
        DurableReceipt receipt = JsonSerializer.Deserialize<DurableReceipt>(payload) ?? throw new InvalidDataException("背景履歴の本文がありません。");
        if (receipt.OperationId != reference.OperationId || !string.Equals(receipt.Fingerprint, reference.Fingerprint, StringComparison.Ordinal)
            || receipt.Outcome != reference.Outcome || !string.Equals(receipt.Reason, reference.Reason, StringComparison.Ordinal)
            || !receipt.Entries.All(entry => DurableRepository.IsShared(entry.Scope)))
        {
            throw new InvalidDataException("背景履歴の本文と存在証明が一致しません。");
        }
        return receipt;
    }

    /// <summary>従来の準備前後の取消差を維持し、別要求・別結果を上書きしません。</summary>
    /// <returns>保持済み結果をそのまま使用できるかです。</returns>
    private static bool Equivalent(DurableReceipt previous, DurableReceipt incoming)
    {
        string aborted = "Persistence.Aborted";
        return string.Equals(ContractJson.Hash(previous), ContractJson.Hash(incoming), StringComparison.Ordinal)
            || previous.Outcome == EOutcome.Failed && incoming.Outcome == EOutcome.Failed && previous.Entries.Count == 0 && incoming.Entries.Count == 0
            && string.Equals(previous.Reason, aborted, StringComparison.Ordinal) && string.Equals(incoming.Reason, aborted, StringComparison.Ordinal)
            && (previous.Fingerprint.Length == 0 || incoming.Fingerprint.Length == 0);
    }

    /// <summary>背景の確認不能を保存し、通常参加と別に公開します。</summary>
    private static void SetProblem(SqliteCommand command, string problem)
    {
        string safe = problem switch
        {
            "Sync.HistoryConflict" => problem,
            "Sync.SnapshotChanged" => problem,
            "Sync.HistoryRecordCapacity" => problem,
            _ => "Sync.HistoryUnavailable",
        };
        command.CommandText = "UPDATE recovery_history SET problem=$problem WHERE kind=$kind AND key=$id AND problem IS NOT $problem";
        command.Parameters.AddWithValue("$problem", safe);
        command.ExecuteNonQuery();
    }
}
