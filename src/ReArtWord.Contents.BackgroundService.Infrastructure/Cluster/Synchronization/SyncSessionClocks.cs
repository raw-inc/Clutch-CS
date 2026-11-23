using System.Collections.Concurrent;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;

/// <summary>固定した両実体の時計だけで同期期限を換算し、CoordinatorやUTCの一致を必要としません。</summary>
internal sealed class SyncSessionClocks(PeerCluster cluster, TimeProvider clock)
{
    private static readonly TimeSpan sampleLifetime = TimeSpan.FromMinutes(1);

    // 管理要求の受付と同じく時計の進み方に最大0.1%の差を見込み、1分間の換算差を上限とします。
    private static readonly long maximumClockLeadTicks = sampleLifetime.Ticks / 1000;
    private readonly ConcurrentDictionary<Guid, Sample> samples = [];

    /// <summary>元の実体の時計を検証できず、日時形式へ切り替えて続行できない理由です。</summary>
    internal static string Unconfirmed => "Sync.ClockUnconfirmed";

    /// <summary>対応照会に応答した実体の経過時間を記録します。未対応との応答と、応答がない状態は区別します。</summary>
    internal void Observe(Guid instance, RequestClockSnapshot? snapshot, long probeStarted)
    {
        if (snapshot is not null && (snapshot.InstanceId != instance || snapshot.ElapsedTicks < 0))
        {
            throw new SyncFailureException(Unconfirmed);
        }
        if (256 <= samples.Count && !samples.ContainsKey(instance))
        {
            samples.TryRemove(samples.MinBy(pair => pair.Value.Received).Key, out _);
        }
        long received = clock.GetTimestamp();
        samples[instance] = new(snapshot, received, clock.GetElapsedTime(probeStarted, received).Ticks);
    }

    /// <summary>背景履歴を取り寄せる側も、最初の固定要求を作る前に相手の時計対応を確認します。</summary>
    /// <returns>同じ実体の応答確認です。</returns>
    internal async Task EnsureAsync(PeerInfo peer, CancellationToken cancellationToken = default)
    {
        if (samples.TryGetValue(peer.InstanceId, out Sample? known) && clock.GetElapsedTime(known.Received) < sampleLifetime)
        {
            return;
        }
        using CancellationTokenSource expiry = new(TimeSpan.FromSeconds(2), clock);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(expiry.Token, cancellationToken);
        long started = clock.GetTimestamp();
        WireMessage? message = await cluster.ExchangeAsync("sync.capabilities", peer.PeerId, peer.InstanceId, JsonSerializer.SerializeToElement(Guid.Empty), linked.Token).ConfigureAwait(false);
        SyncResponse reply = message?.Body.Deserialize<SyncResponse>() ?? throw new SyncFailureException(Unconfirmed);
        if (reply.Reason is not null)
        {
            throw new SyncFailureException(reply.Reason);
        }
        Observe(peer.InstanceId, reply.Clock, started);
    }

    /// <summary>相手の応答受信後に進んだ時間だけを加え、応答の通信遅延ぶんは期限を早めます。</summary>
    /// <returns>新旧両端の対応を確認できた固定期限です。未対応の場合は従来の日時形式を使います。</returns>
    internal SyncTiming? Create(Guid targetInstance, SyncLifetime lifetime)
    {
        if (!samples.TryGetValue(targetInstance, out Sample? sample) || sample.Clock is not { } target)
        {
            return null;
        }
        long remaining = lifetime.Remaining.Ticks;
        if (remaining <= 0)
        {
            throw new SyncFailureException("Sync.SessionExpired");
        }
        long source = DeadlineTicks((Int128)cluster.AdmissionClock.Capture().ElapsedTicks + remaining);
        long destination = DeadlineTicks((Int128)target.ElapsedTicks + clock.GetElapsedTime(sample.Received).Ticks + remaining);
        return new(cluster.LocalInstanceId, source, targetInstance, destination, lifetime.Duration.Ticks, sample.RoundTripTicks);
    }

    /// <summary>返送ファイルにも元の両端の期限を引き継ぎ、作成が終わった時点から有効期間をやり直しません。</summary>
    /// <returns>必要なら短縮した、向きを入れ替えた期限です。</returns>
    internal SyncTiming? Reverse(SyncTiming? original, Guid targetInstance, SyncLifetime lifetime)
    {
        if (original is null)
        {
            return Create(targetInstance, lifetime);
        }
        if (original.TargetInstanceId != cluster.LocalInstanceId || original.SourceInstanceId != targetInstance)
        {
            throw new SyncFailureException("Sync.ManifestMismatch");
        }
        long source = Math.Min(original.TargetUntilTicks, checked(cluster.AdmissionClock.Capture().ElapsedTicks + lifetime.Remaining.Ticks));
        long shortened = original.TargetUntilTicks - source;

        // 最初の時計照会で実際にかかった往復時間を差し引き、逆方向でも発行元より
        // 長い期限へ換算しません。元の残り時間を超える延長には使いません。
        long destination = DeadlineTicks((Int128)original.SourceUntilTicks - shortened - original.ClockUncertaintyTicks);
        return new(cluster.LocalInstanceId, source, targetInstance, destination,
            Math.Min(original.MaximumLifetimeTicks, lifetime.Duration.Ticks), original.ClockUncertaintyTicks);
    }

    /// <summary>宛先実体に固定された期限を自分の経過時間で検査します。旧形式の日時は受入時に一度だけ換算します。</summary>
    /// <returns>受入後に増えないローカルの残り時間です。</returns>
    internal SyncLifetime Accept(SyncTiming? timing, DateTimeOffset legacyExpires, Guid sourceInstance, TimeSpan maximum)
    {
        TimeSpan remaining;
        if (timing is null)
        {
            remaining = legacyExpires - clock.GetUtcNow();
        }
        else
        {
            if (timing.SourceInstanceId != sourceInstance || timing.TargetInstanceId != cluster.LocalInstanceId
                || timing.MaximumLifetimeTicks <= 0 || maximum.Ticks < timing.MaximumLifetimeTicks
                || timing.SourceUntilTicks < 0 || timing.TargetUntilTicks < 0 || timing.ClockUncertaintyTicks < 0
                || TimeSpan.FromHours(1).Ticks < timing.ClockUncertaintyTicks)
            {
                throw new SyncFailureException("Sync.ManifestMismatch");
            }
            remaining = TimeSpan.FromTicks(checked(timing.TargetUntilTicks - cluster.AdmissionClock.Capture().ElapsedTicks));
            if (timing.MaximumLifetimeTicks < remaining.Ticks)
            {
                if (maximumClockLeadTicks < remaining.Ticks - timing.MaximumLifetimeTicks)
                {
                    throw new SyncFailureException("Sync.ManifestMismatch");
                }

                // 推定した期限がわずかに先行した場合は、送信元が指定した有効期間までに収めます。
                remaining = TimeSpan.FromTicks(timing.MaximumLifetimeTicks);
            }
        }
        if (remaining <= TimeSpan.Zero || maximum < remaining)
        {
            throw new SyncFailureException("Sync.SessionExpired");
        }
        return new(clock, remaining);
    }

    /// <summary>未検証の時計値の加算が範囲外になっても、当該同期の確認不能として扱います。</summary>
    /// <returns>表現可能な固定期限です。</returns>
    private static long DeadlineTicks(Int128 value)
    {
        if (value < 0 || long.MaxValue < value)
        {
            throw new SyncFailureException(Unconfirmed);
        }
        return (long)value;
    }

    /// <summary>同じプロセスの単調時計はUTC補正では変わりません。再起動は実体IDの変更で区別します。</summary>
    private sealed record Sample(RequestClockSnapshot? Clock, long Received, long RoundTripTicks);
}
