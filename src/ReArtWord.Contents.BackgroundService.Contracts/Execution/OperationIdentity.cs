using System.Collections.Concurrent;

namespace ReArtWord.Contents.BackgroundService.Contracts.Execution;

/// <summary>新規操作を時計から切り離し、保存済みの旧IDは元の形式で照合します。</summary>
public static class OperationIdentity
{
    private static readonly OperationIdSequence sharedWrites = new();
    private static readonly OperationIdSequence localWrites = new();
    private static readonly ConcurrentDictionary<string, OperationIdSequence> requests = new(StringComparer.Ordinal);
    private static readonly OperationIdSequence executions = new();

    /// <summary>新しく開始する保存操作のIDを発行します。保存済み要求の再発行には使いません。</summary>
    /// <param name="shared">全変更が共有対象ならtrueです。ローカル変更には別の連番を使い、共有退役区間に不要な欠番を作りません。</param>
    /// <returns>発行元と連番を持つUUIDv8です。</returns>
    public static Guid Create(bool shared = true) => shared ? sharedWrites.Next()
                                                            : localWrites.Next();

    /// <summary>永続受付する要求には保存操作と別の連番を使い、要求と内部書込みの欠番を混ぜません。</summary>
    /// <param name="purpose">登録された操作名です。保存先の異なる用途を同じ連番へ混ぜないために使用します。</param>
    /// <returns>新規要求のUUIDv8です。有限な再送期間にUUIDv7を使う実行要求には使用しません。</returns>
    public static Guid CreateDurableRequest(string purpose = "operation")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        return requests.GetOrAdd(purpose, static _ => new()).Next();
    }

    /// <summary>ノード実行とその復旧記録を特定します。内部書込みや通信受付とは別の連番を使います。</summary>
    /// <returns>新規ノード実行のUUIDv8です。保存済みの実行には元のIDを使用します。</returns>
    public static Guid CreateExecution() => executions.Next();

    /// <summary>旧形式を推測した時刻へ置き換えず、時刻付きIDだけを読みます。</summary>
    /// <returns>妥当な発行時刻を持つ場合だけtrueです。</returns>
    public static bool TryGetIssuedAt(Guid id, out DateTimeOffset issuedAt)
    {
        issuedAt = default;
        Span<byte> bytes = stackalloc byte[16];
        _ = id.TryWriteBytes(bytes, bigEndian: true, out _);
        if ((bytes[6] >> 4) != 7 || (bytes[8] & 192) != 128)
        {
            return false;
        }
        long milliseconds = 0;
        for (int index = 0; index < 6; index++)
        {
            milliseconds = (milliseconds << 8) | bytes[index];
        }
        if (DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() < milliseconds)
        {
            return false;
        }
        issuedAt = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        return true;
    }
}
