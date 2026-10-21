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
            return now < deadline && request.ExpiresAt <= deadline && deadline <= now.AddMinutes(10);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}
