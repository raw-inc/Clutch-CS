namespace ReArtWord.Contents.BackgroundService.Contracts.Execution;

/// <summary>新規操作を時計から切り離し、保存済みの旧IDは元の形式で照合します。</summary>
public static class OperationIdentity
{
    private static readonly OperationIdSequence writes = new();
    private static readonly OperationIdSequence requests = new();

    /// <summary>新しく開始する保存操作のIDを発行します。保存済み要求の再発行には使いません。</summary>
    /// <returns>発行元と連番を持つUUIDv8です。</returns>
    public static Guid Create() => writes.Next();

    /// <summary>要求IDは保存操作と別の連番にし、保存されない照会で保存操作の連番を消費しません。</summary>
    /// <returns>新規要求のUUIDv8です。再送時は元の要求IDを使用します。</returns>
    public static Guid CreateRequest() => requests.Next();

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
