using System.Collections.Concurrent;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

/// <summary>確認した相手の時計へ期限を換算し、元の本文・要求ID・結果照会用の指紋を保持します。</summary>
public sealed class RequestClockClient(TimeProvider clock)
{
    private readonly ConcurrentDictionary<Guid, Calibration> clocks = [];
    private readonly Lock timeGate = new();
    private DateTimeOffset originNow = clock.GetUtcNow();
    private long originTimestamp = clock.GetTimestamp();

    /// <summary>対応未確認を旧版とみなさず、明示的に未対応と返った場合だけ元形式を使います。</summary>
    /// <returns>時計差を補正した配送本文。未対応の場合はnullです。</returns>
    public async Task<ClockedClusterRequest?> PrepareAsync(ClusterRequest request,
        Func<ClusterRequest, CancellationToken, Task<ClusterResponse>> inspect, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(inspect);
        if (!clocks.TryGetValue(request.TargetInstanceId, out Calibration? calibration) || TimeSpan.FromMinutes(1) <= clock.GetElapsedTime(calibration.ReceivedAt))
        {
            ClusterRequest probe = new(Guid.NewGuid(), RequestClockSnapshot.Capability, request.TargetPeerId, request.TargetInstanceId, request.ExpiresAt, NodeValue.Null);
            ClusterResponse response = await inspect(probe, cancellationToken).ConfigureAwait(false);
            long receivedAt = clock.GetTimestamp();
            RequestClockSnapshot? snapshot = null;
            string unavailable = "Cluster.InspectionTargetUnavailable";
            if (response.Outcome == EOutcome.Succeeded)
            {
                snapshot = response.Value?.Json.Deserialize<RequestClockSnapshot>(ContractJson.Options);
                if (snapshot is null || snapshot.InstanceId != request.TargetInstanceId || snapshot.ElapsedTicks < 0)
                {
                    throw new InvalidDataException("The admission clock could not be verified.");
                }
            }
            else if (response.Outcome != EOutcome.NotExecuted || !string.Equals(response.Reason, unavailable, StringComparison.Ordinal))
            {
                throw new IOException("The admission clock response is unconfirmed.");
            }
            calibration = new(snapshot, receivedAt);
            foreach (Guid expired in clocks.Where(pair => TimeSpan.FromMinutes(1) <= clock.GetElapsedTime(pair.Value.ReceivedAt)).Select(pair => pair.Key))
            {
                clocks.TryRemove(expired, out _);
            }
            if (clocks.Count < 256)
            {
                clocks[request.TargetInstanceId] = calibration;
            }
        }
        if (calibration.Snapshot is not { } target)
        {
            return null;
        }
        DateTimeOffset sourceNow = CaptureOriginNow();
        TimeSpan remaining = request.ExpiresAt - sourceNow;
        if (request.Admission is { } inherited)
        {
            remaining = inherited.Remaining;
            sourceNow = request.ExpiresAt - remaining;
        }

        // 応答を受け取った時点に「相手が応答した時点」を対応させます。
        // 復路の所要時間だけ早い期限となり、遅れて届いた要求の受付期間を延長しません。
        Int128 startBy = (Int128)target.ElapsedTicks + clock.GetElapsedTime(calibration.ReceivedAt).Ticks + remaining.Ticks;
        if (startBy < long.MinValue || long.MaxValue < startBy)
        {
            throw new InvalidDataException("The admission clock is outside its supported range.");
        }
        return new(request, target.InstanceId, (long)startBy, sourceNow);
    }

    /// <summary>送信元の時計を戻しても、既存要求の開始期限を再び有効にしません。</summary>
    /// <returns>この送信元で一度消費した時間を戻さない日時です。</returns>
    private DateTimeOffset CaptureOriginNow()
    {
        using (timeGate.EnterScope())
        {
            long timestamp = clock.GetTimestamp();
            DateTimeOffset elapsedNow = originNow + clock.GetElapsedTime(originTimestamp, timestamp);
            DateTimeOffset wallNow = clock.GetUtcNow();
            originNow = elapsedNow;
            if (elapsedNow < wallNow)
            {
                originNow = wallNow;
            }
            originTimestamp = timestamp;
            return originNow;
        }
    }

    /// <summary>未対応の確認結果も相手実体に結び付けて保持します。</summary>
    private sealed record Calibration(RequestClockSnapshot? Snapshot, long ReceivedAt);
}
