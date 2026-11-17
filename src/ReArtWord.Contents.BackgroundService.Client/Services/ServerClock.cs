using System.Globalization;
using System.Net.Http.Headers;
using ReArtWord.Contents.BackgroundService.Contracts.Management;

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
    private long sequence;
    private long instanceRequestStarted;
    private long requestSequence;
    private long instanceRequestOrder;

    /// <summary>単調時計の分解能内で同時に始まったHTTP要求にも、異なる順序番号を付けます。</summary>
    /// <returns>このブラウザーの時計対応で使用する要求順序です。</returns>
    internal long NextRequestOrder() => Interlocked.Increment(ref requestSequence);

    /// <summary>要求開始時の単調時刻です。</summary>
    internal long Timestamp => clock.GetTimestamp();

    /// <summary>ブラウザー側の時刻補正と切り離して、操作の準備にかかった時間を返します。</summary>
    /// <returns>指定した起点からの経過時間です。</returns>
    internal TimeSpan ElapsedSince(long started) => clock.GetElapsedTime(started);

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
    internal void Observe(HttpResponseHeaders headers, long started, Guid requestId = default, long requestOrder = 0)
    {
        string format = "O";
        if (!headers.TryGetValues(ObservationHeaders.ObservedAt, out IEnumerable<string>? times)
            || !headers.TryGetValues(ObservationHeaders.Instance, out IEnumerable<string>? identities)
            || !DateTimeOffset.TryParseExact(times.FirstOrDefault(), format, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset time)
            || !Guid.TryParse(identities.FirstOrDefault(), out Guid host))
        {
            return;
        }
        long incomingSequence = 0;
        if (headers.TryGetValues(ObservationHeaders.Sequence, out IEnumerable<string>? numbers)
            && (!long.TryParse(numbers.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out incomingSequence) || incomingSequence <= 0
                || requestId == Guid.Empty || !headers.TryGetValues(ObservationHeaders.Request, out IEnumerable<string>? requests)
                || !Guid.TryParse(requests.FirstOrDefault(), out Guid echoed) || echoed != requestId))
        {
            return;
        }
        using (gate.EnterScope())
        {
            bool earlierRequest = 0 < requestOrder && 0 < instanceRequestOrder ? requestOrder < instanceRequestOrder
                                                                               : started < instanceRequestStarted;

            // 新しいHostの応答を確認した後に、以前の要求の遅着応答へ戻りません。
            // 照合IDは別のHTTP要求のキャッシュを除外し、同じHost内の順序は日時ではなく連番で判定します。
            if (latest.HasValue && (host != instance && earlierRequest
                || host == instance && (0 < sequence && incomingSequence <= sequence
                    || sequence == 0 && incomingSequence == 0 && time <= latest.Value)))
            {
                return;
            }
            long now = clock.GetTimestamp();
            anchor = time + clock.GetElapsedTime(started, now);
            received = now;
            latest = time;
            if (host != instance)
            {
                instanceRequestStarted = started;
                instanceRequestOrder = requestOrder;
            }
            instance = host;
            sequence = incomingSequence;
        }
    }
}
