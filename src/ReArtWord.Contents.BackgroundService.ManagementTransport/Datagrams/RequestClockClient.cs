using System.Collections.Concurrent;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

/// <summary>送信先Coreへ経過時間を照会し、その時計で表した受付期限を作成します。要求本文とIDは変更しません。</summary>
public sealed class RequestClockClient(TimeProvider clock)
{
    private readonly ConcurrentDictionary<Guid, Calibration> clocks = [];
    private readonly Lock timeGate = new();
    private DateTimeOffset originNow = clock.GetUtcNow();
    private long originTimestamp = clock.GetTimestamp();

    /// <summary>時計の照会結果を再利用できる最長時間です。</summary>
    internal static TimeSpan CalibrationLifetime => TimeSpan.FromMinutes(1);

    /// <summary>時計照会に未対応との応答がある場合だけ旧形式を使います。通信失敗を未対応とみなすと、時計差のある要求を旧形式で送ってしまうためです。</summary>
    /// <returns>元の要求に換算済みの期限を添えたデータです。送信先が未対応の場合はnullです。</returns>
    public async Task<ClockedClusterRequest?> PrepareAsync(ClusterRequest request,
        Func<ClusterRequest, CancellationToken, Task<ClusterResponse>> inspect, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(inspect);
        if (!clocks.TryGetValue(request.TargetInstanceId, out Calibration? calibration) || CalibrationLifetime <= clock.GetElapsedTime(calibration.ReceivedAt))
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
            foreach (Guid expired in clocks.Where(pair => CalibrationLifetime <= clock.GetElapsedTime(pair.Value.ReceivedAt)).Select(pair => pair.Key))
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

        // Coreが返した経過時間に、応答受信後の経過時間と要求の残り時間を加えます。
        // Coreが応答してからこちらに届くまでの時間は加えないため、期限はその分だけ早くなります。
        // 通信の遅れによって、本来は期限切れの要求を新たに受け付けることを防ぎます。
        Int128 startBy = (Int128)target.ElapsedTicks + clock.GetElapsedTime(calibration.ReceivedAt).Ticks + remaining.Ticks;
        if (startBy < long.MinValue || long.MaxValue < startBy)
        {
            throw new InvalidDataException("The admission clock is outside its supported range.");
        }
        return new(request, target.InstanceId, (long)startBy, sourceNow);
    }

    /// <summary>OSの時計が戻っても期限切れの要求を再び有効にしないよう、経過時間から求めた日時より前には戻しません。</summary>
    /// <returns>前回からの経過時間を加えた日時と、現在のUTC日時のうち、遅い方です。</returns>
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

    /// <summary>照会結果と受信時点の単調時計を保持します。Snapshotがnullの場合は未対応を確認済みです。</summary>
    private sealed record Calibration(RequestClockSnapshot? Snapshot, long ReceivedAt);
}
