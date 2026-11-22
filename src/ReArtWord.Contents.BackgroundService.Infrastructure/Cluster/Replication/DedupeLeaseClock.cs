using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.State;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Replication;

/// <summary>確保したピアの経過時計と、同じ保存版を観測してからの時間で共有キーのTTLを判定します。</summary>
internal sealed class DedupeLeaseClock(PeerCluster cluster, IDurableRepository repository)
{
    private static string LeaseField => "dedupeLease";
    private static string LegacyExpiry => "expiresAt";
    private static string LegacyStart => "reservedAt";
    private static TimeSpan Maximum => TimeSpan.FromMilliseconds(int.MaxValue - 1);

    /// <summary>予約ごとに異なるIDを含め、分断された側で独立に行った確保を同じ値にまとめません。</summary>
    /// <returns>旧Coreでは無期限に見えるマーカーです。旧Coreの進んだ時計による早期再取得を防ぎます。</returns>
    internal NodeValue Create(Guid id, long? ticks)
    {
        DateTimeOffset reservedAt = DateTimeOffset.UtcNow;
        if (ticks is not { } duration)
        {
            return NodeValue.FromObject(new { reservedAt, expiresAt = (DateTimeOffset?)null });
        }
        RequestClockSnapshot sample = cluster.AdmissionClock.Capture();
        Lease lease = new(id, cluster.LocalPeerId, sample.InstanceId, duration, checked(sample.ElapsedTicks + duration), SystemBootObservation.Read());
        return NodeValue.FromObject(new { reservedAt, expiresAt = (DateTimeOffset?)null, dedupeLease = lease });
    }

    /// <summary>同じマーカーの期限を確認できた場合だけ有効・失効を返します。旧版の絶対日時は他PCの時計と比較しません。</summary>
    /// <returns>失効ならtrue、有効ならfalse、どちらも証明できない間はnullです。</returns>
    internal async Task<bool?> ExpiredAsync(string scope, string key, VersionedState previous, bool detached, CancellationToken cancellationToken = default)
    {
        JsonElement value = previous.Value.Json;
        if (previous.Value.Kind != EValueKind.Json || value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        Lease? lease = null;
        TimeSpan lifetime;
        if (value.TryGetProperty(LeaseField, out JsonElement item))
        {
            try
            {
                lease = item.Deserialize<Lease>(ContractJson.Options);
            }
            catch (JsonException)
            {
                return null;
            }
            if (lease is null || lease.Instance == Guid.Empty || lease.Id == Guid.Empty || lease.Duration <= 0 || Maximum.Ticks < lease.Duration || lease.End < lease.Duration)
            {
                return null;
            }
            lifetime = TimeSpan.FromTicks(lease.Duration);
            if (lease.Instance == cluster.LocalInstanceId)
            {
                return lease.End <= cluster.AdmissionClock.Capture().ElapsedTicks;
            }
            if (lease.Boot is { } issued && SystemBootObservation.Read() is { } current && string.Equals(lease.Peer, cluster.LocalPeerId, StringComparison.Ordinal)
                && string.Equals(issued.Id, current.Id, StringComparison.Ordinal) && issued.Uptime <= current.Uptime)
            {
                return lifetime <= current.Uptime - issued.Uptime;
            }
        }
        else
        {
            if (!value.TryGetProperty(LegacyExpiry, out JsonElement end) || end.ValueKind == JsonValueKind.Null)
            {
                return false;
            }
            if (end.ValueKind != JsonValueKind.String || !end.TryGetDateTimeOffset(out DateTimeOffset until)
                || !value.TryGetProperty(LegacyStart, out JsonElement start) || start.ValueKind != JsonValueKind.String || !start.TryGetDateTimeOffset(out DateTimeOffset from)
                || until <= from || Maximum < until - from)
            {
                return null;
            }
            lifetime = until - from;
        }
        return await InspectForeignAsync(scope, key, previous.Version, lease, lifetime, detached, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>確定版を観測した時点で元TTLは既に始まっています。発行元が不在でも、この上限を超えれば失効を確認できます。</summary>
    /// <returns>証拠のない期間だけ確認不能を返します。</returns>
    private async Task<bool?> InspectForeignAsync(string scope, string key, string version, Lease? lease, TimeSpan lifetime, bool detached, CancellationToken cancellationToken = default)
    {
        string localScope = "peer:" + cluster.LocalPeerId + ":dedupe-observations";
        string address = ContractJson.Hash(new { scope, key });
        SystemBootSession now = LocalTime();
        DurableEntry? saved = await repository.ReadAsync(localScope, address, cancellationToken).ConfigureAwait(false);
        Observation? known = saved?.Value?.Json.Deserialize<Observation>(ContractJson.Options);
        bool sameVersion = known is not null && string.Equals(known.Version, version, StringComparison.Ordinal);
        if (sameVersion && known is not null)
        {
            if (string.Equals(known.Boot.Id, now.Id, StringComparison.Ordinal))
            {
                TimeSpan age = now.Uptime - known.Boot.Uptime;
                if (known.Latest <= age)
                {
                    return true;
                }
                if (TimeSpan.Zero <= age && age < known.Earliest)
                {
                    return false;
                }
            }
            else if (SystemBootObservation.Read() is not null && known.Latest <= now.Uptime && !known.Boot.Id.StartsWith(RuntimePrefix, StringComparison.Ordinal))
            {
                return true;
            }
        }
        Observation bound = new(version, now, TimeSpan.Zero, lifetime);
        if (!detached && lease is not null && cluster.GetPeers().Any(peer => peer.InstanceId == lease.Instance && Contracts.Cluster.PeerStatusRules.CanParticipate(peer.Status)))
        {
            ClusterRequest probe = ClusterRequest.Start(RequestClockSnapshot.Capability, lease.Peer, lease.Instance, TimeSpan.FromSeconds(5), NodeValue.Null);
            long sent = TimeProvider.System.GetTimestamp();
            ClusterResponse response;
            try
            {
                response = await cluster.InspectAsync(probe, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                response = new(EOutcome.Unknown);
            }
            if (response.Outcome == EOutcome.Succeeded && response.Value?.Json.Deserialize<RequestClockSnapshot>(ContractJson.Options) is { } sample
                && sample.InstanceId == lease.Instance && 0 <= sample.ElapsedTicks)
            {
                TimeSpan upper = TimeSpan.FromTicks(lease.End - sample.ElapsedTicks);
                if (upper <= TimeSpan.Zero)
                {
                    return true;
                }

                // 応答が遅れている可能性を、期限内と確定できる側からだけ差し引きます。
                bound = new(version, LocalTime(), upper - TimeProvider.System.GetElapsedTime(sent), upper);
            }
        }
        if (sameVersion && known is not null)
        {
            bool sameBoot = string.Equals(bound.Boot.Id, known.Boot.Id, StringComparison.Ordinal);
            bool osRebooted = !sameBoot && !bound.Boot.Id.StartsWith(RuntimePrefix, StringComparison.Ordinal) && !known.Boot.Id.StartsWith(RuntimePrefix, StringComparison.Ordinal);
            TimeSpan elapsed = bound.Boot.Uptime;
            if (sameBoot)
            {
                elapsed -= known.Boot.Uptime;
            }
            if ((sameBoot || osRebooted) && TimeSpan.Zero <= elapsed && known.Latest - elapsed < bound.Latest)
            {
                bound = bound with { Latest = known.Latest - elapsed };
            }
        }
        if (!sameVersion || TimeSpan.Zero < bound.Earliest || !string.Equals(known?.Boot.Id, now.Id, StringComparison.Ordinal))
        {
            // 別プロセスでの初回観測が競合しても、先に保存された起点を上書きしません。
            DurableReceipt receipt = await repository.ApplyBatchAsync(OperationIdentity.Create(), [new DurableMutation(localScope, address, saved?.Version, NodeValue.FromObject(bound))], cancellationToken).ConfigureAwait(false);
            if (receipt.Outcome != EOutcome.Succeeded)
            {
                return null;
            }
        }
        if (TimeSpan.Zero < bound.Earliest)
        {
            return false;
        }
        return null;
    }

    private static string RuntimePrefix => "runtime:";

    /// <summary>同じOS起動をまたぐCoreの再起動でも上限を引き継ぎます。</summary>
    /// <returns>OS起動回が不明な環境では現在のCore実体でのみ比較できる時間です。</returns>
    private SystemBootSession LocalTime() => SystemBootObservation.Read() ?? new(RuntimePrefix + cluster.LocalInstanceId.ToString("N"), TimeSpan.FromTicks(cluster.AdmissionClock.Capture().ElapsedTicks));

    /// <summary>元の確保実体で指定された期限です。</summary>
    private sealed record Lease(Guid Id, string Peer, Guid Instance, long Duration, long End, SystemBootSession? Boot);

    /// <summary>このピアが同じ保存版について確認した、失効までの時間幅です。</summary>
    private sealed record Observation(string Version, SystemBootSession Boot, TimeSpan Earliest, TimeSpan Latest);
}
