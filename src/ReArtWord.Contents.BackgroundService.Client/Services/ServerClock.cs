using System.Globalization;
using System.Net.Http.Headers;

namespace ReArtWord.Contents.BackgroundService.Client.Services;

/// <summary>受信後の単調経過時間を使い、閲覧PCの時計差を監視の欠測へ変換しません。</summary>
internal sealed class ServerClock(TimeProvider? provider = null)
{
    private readonly TimeProvider clock = provider ?? TimeProvider.System;
    private readonly Lock gate = new();
    private Guid instance;
    private DateTimeOffset? latest;
    private DateTimeOffset anchor;
    private long received;

    /// <summary>要求開始時の単調時刻です。</summary>
    internal long Timestamp => clock.GetTimestamp();

    /// <summary>ClientHostの時刻軸で現在の観測年齢を判定します。</summary>
    internal DateTimeOffset UtcNow
    {
        get
        {
            using (gate.EnterScope())
            {
                return latest.HasValue ? anchor + clock.GetElapsedTime(received)
                                       : clock.GetUtcNow();
            }
        }
    }

    /// <summary>古いキャッシュ応答では時計を巻き戻さず、通信待ち時間も観測年齢へ含めます。</summary>
    internal void Observe(HttpResponseHeaders headers, long started)
    {
        string timeHeader = "X-Contents-Observed-At", instanceHeader = "X-Contents-Host-Instance", format = "O";
        if (!headers.TryGetValues(timeHeader, out IEnumerable<string>? times)
            || !headers.TryGetValues(instanceHeader, out IEnumerable<string>? identities)
            || !DateTimeOffset.TryParseExact(times.FirstOrDefault(), format, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset time)
            || !Guid.TryParse(identities.FirstOrDefault(), out Guid host))
        {
            return;
        }
        using (gate.EnterScope())
        {
            if (host == instance && latest.HasValue && time <= latest.Value)
            {
                return;
            }
            long now = clock.GetTimestamp();
            anchor = time + clock.GetElapsedTime(started, now);
            received = now;
            latest = time;
            instance = host;
        }
    }
}
