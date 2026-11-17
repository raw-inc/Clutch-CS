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
                // UUIDの時刻と送信日時は同じPCの時計です。受信後に期限を付け直すのではなく、
                // 残りのID有効期間を受信側で保持し、通信待ちの分だけ早く重複記録を捨てないようにします。
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
