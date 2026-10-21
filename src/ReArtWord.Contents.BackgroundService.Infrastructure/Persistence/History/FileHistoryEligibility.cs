using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

/// <summary>実ファイルの予約・補償に必要な現在値と、終了後の参照履歴を区別します。</summary>
internal static class FileHistoryEligibility
{
    /// <summary>現役予約の台帳を含めず、操作ごとの履歴だけを対象にします。</summary>
    /// <returns>共通の操作履歴期間を適用する用途かです。</returns>
    internal static bool IsScope(string? scope) => scope is "file:transactions" || scope?.EndsWith(":file-transactions", StringComparison.Ordinal) == true
        || scope?.EndsWith(":file-recovery", StringComparison.Ordinal) == true || scope?.EndsWith(":file-prepared", StringComparison.Ordinal) == true
        || scope?.EndsWith(":file-gate-requests", StringComparison.Ordinal) == true;

    /// <summary>経過時間だけでは未知の副作用や保持中の退避・予約を削除しません。</summary>
    /// <returns>この履歴を圧縮・期限削除しても現役処理の復旧証拠を失わないかです。</returns>
    internal static bool Completed(DurableEntry entry)
    {
        JsonElement value = entry.Value!.Json;
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        string recovery = ":file-recovery", prepared = ":file-prepared", requests = ":file-gate-requests", resultName = "Result", outcomeName = "Outcome";
        if (entry.Scope.EndsWith(recovery, StringComparison.Ordinal))
        {
            return HistoryJson.TryGet(value, "release", out JsonElement release) && Succeeded(release);
        }
        if (entry.Scope.EndsWith(prepared, StringComparison.Ordinal))
        {
            return HistoryJson.TryGet(value, "Released", out JsonElement released) && released.ValueKind == JsonValueKind.True;
        }
        if (entry.Scope.EndsWith(requests, StringComparison.Ordinal))
        {
            return Succeeded(value);
        }
        if (!HistoryJson.TryGet(value, resultName, out JsonElement result) || result.ValueKind != JsonValueKind.Object
            || !HistoryJson.TryGet(result, outcomeName, out JsonElement outcome) || !HistoryJson.Final(outcome))
        {
            return false;
        }
        return HistoryJson.TryGet(value, "Lease", out JsonElement lease) && lease.ValueKind == JsonValueKind.Null
            || HistoryJson.TryGet(result, "ReservationRelease", out JsonElement reservation) && Succeeded(reservation);
    }

    /// <summary>知らない形式や未確認の結果を、解放完了として推測しません。</summary>
    /// <returns>成功の明示があるかです。</returns>
    private static bool Succeeded(JsonElement value) => value.ValueKind == JsonValueKind.Object
        && HistoryJson.TryGet(value, "Outcome", out JsonElement outcome) && outcome.ValueKind == JsonValueKind.Number
        && outcome.TryGetInt32(out int number) && number == (int)EOutcome.Succeeded;
}
