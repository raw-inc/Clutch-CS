using System.Globalization;
using System.Net;
using ReArtWord.Contents.BackgroundService.Client.Services;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>閲覧PCの時計差、通信遅延、古いキャッシュ応答を監視の鮮度と区別します。</summary>
public sealed class ServerClockTests
{
    /// <summary>端末の時計を前後へずらしても観測元の時刻と単調時間で判定します。</summary>
    [Theory]
    [InlineData(-3600)]
    [InlineData(3600)]
    public void ClientClockSkewAndCachedRepliesDoNotRenewOldObservations(int skewSeconds)
    {
        DateTimeOffset server = DateTimeOffset.UtcNow;
        ManualClock local = new(server.AddSeconds(skewSeconds));
        ServerClock clock = new(local);
        Guid instance = Guid.NewGuid();
        long started = clock.Timestamp;
        local.Advance(TimeSpan.FromSeconds(2));
        using HttpResponseMessage response = Response(server, instance);
        clock.Observe(response.Headers, started);
        Assert.Equal(server.AddSeconds(2), clock.UtcNow);
        local.Advance(TimeSpan.FromSeconds(20));
        clock.Observe(response.Headers, clock.Timestamp);
        Assert.Equal(server.AddSeconds(22), clock.UtcNow);
        using HttpResponseMessage restarted = Response(server.AddSeconds(30), Guid.NewGuid());
        clock.Observe(restarted.Headers, clock.Timestamp);
        Assert.Equal(server.AddSeconds(30), clock.UtcNow);
    }

    /// <summary>元サーバーで生成した時刻と実体を返します。</summary>
    /// <returns>呼出側が解放する応答です。</returns>
    private static HttpResponseMessage Response(DateTimeOffset time, Guid instance)
    {
        HttpResponseMessage response = new(HttpStatusCode.OK);
        response.Headers.Add("X-Contents-Observed-At", time.ToString("O", CultureInfo.InvariantCulture));
        response.Headers.Add("X-Contents-Host-Instance", instance.ToString("D"));
        return response;
    }

    /// <summary>OS時刻とは独立して経過時間だけを進める時計です。</summary>
    private sealed class ManualClock(DateTimeOffset initial) : TimeProvider
    {
        private long ticks;

        /// <summary>試験の単調時計はTimeSpanと同じ周波数です。</summary>
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        /// <summary>経過tickを返します。</summary>
        /// <returns>単調時計です。</returns>
        public override long GetTimestamp() => ticks;

        /// <summary>意図的にずれた閲覧PCの時刻を返します。</summary>
        /// <returns>ローカル時刻です。</returns>
        public override DateTimeOffset GetUtcNow() => initial.AddTicks(ticks);

        /// <summary>待機せず時間経過を与えます。</summary>
        internal void Advance(TimeSpan elapsed) => ticks += elapsed.Ticks;
    }
}
