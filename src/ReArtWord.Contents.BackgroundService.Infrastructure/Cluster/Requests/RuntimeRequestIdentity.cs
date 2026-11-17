using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Requests;

/// <summary>受付期限をIDの生成時刻で上限固定し、保持期限後の同じIDを新しい実行にしません。</summary>
internal static class RuntimeRequestIdentity
{
    /// <summary>UUIDv7の時刻から、要求本文で延長できない照合保持期限を求めます。</summary>
    /// <returns>有限期限の通常実行要求として扱える場合だけtrueです。</returns>
    internal static bool TryDeadline(ClusterRequest request, DateTimeOffset now, out DateTimeOffset deadline)
    {
        Span<byte> bytes = stackalloc byte[16];
        _ = request.Id.TryWriteBytes(bytes, bigEndian: true, out _);
        deadline = default;
        if ((bytes[6] >> 4) != 7)
        {
            return false;
        }
        long milliseconds = 0;
        for (int index = 0; index < 6; index++)
        {
            milliseconds = (milliseconds << 8) | bytes[index];
        }
        try
        {
            deadline = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).AddMinutes(6);
            DateTimeOffset sourceNow = request.Admission?.SourceNow ?? now;
            bool valid = sourceNow < deadline && request.ExpiresAt <= deadline && deadline <= sourceNow.AddMinutes(10);
            if (valid && request.Admission is not null)
            {
                // IDの有効期間はUUIDv7の生成日時から6分で固定し、要求元の時刻系で検証します。
                // 受信側の時計と直接比較すると、時計差で重複実行防止の記録を早く捨てるおそれがあります。
                // そこで、送信側の換算時点で残っていた保持時間を受信側の時刻系へ移します。
                // 通信中の時間も保持に含めるため、必要な期間より早く記録を削除することはありません。
                deadline = now + (deadline - sourceNow);
            }
            return valid;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}
