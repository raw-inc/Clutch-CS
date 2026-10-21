using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Local;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Health;

/// <summary>UDP受理だけでなくPeerと新実体のreadyを確認します。</summary>
internal static class UpdateHealthProbe
{
    /// <summary>明示UDPで新しい起動回の健康状態を確認します。</summary>
    /// <returns>期限内に確認した状態と最後に取得できた実プロセスです。</returns>
    internal static async Task<UpdateHealthResult> WaitAsync(LocalUpdateJob job, ProcessIdentity? startedProcess, CancellationToken cancellationToken = default)
    {
        UpdateObserver observer = new(UpdateObserver.Binding(job.Options), job.Options.ClusterId!);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = observer.ConfigureAwait(false);
        using CancellationTokenSource timeout = new(TimeSpan.FromMilliseconds(job.Options.StartTimeoutMilliseconds));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        ProcessIdentity? identity = startedProcess;
        Guid? lastInstance = null;
        try
        {
            while (!linked.IsCancellationRequested)
            {
                try
                {
                    Contracts.Cluster.PeerInfo observed = await observer.FindAsync(job.PeerId, cancellationToken: linked.Token).ConfigureAwait(false);
                    Contracts.Management.ManagementReply response = await observer.SendAsync(job.PeerId, observed.InstanceId, "health", null, true, linked.Token).ConfigureAwait(false);
                    if (response.Outcome == Contracts.Execution.EOutcome.Succeeded && response.Value is not null)
                    {
                        JsonElement body = response.Value.Json;
                        bool samePeer = body.TryGetProperty("peerId", out JsonElement peer) && string.Equals(peer.GetString(), job.PeerId, StringComparison.Ordinal);
                        bool newInstance = body.TryGetProperty("instanceId", out JsonElement instanceValue) && instanceValue.TryGetGuid(out Guid instance) && instance == observed.InstanceId && instance != job.PreviousInstanceId;
                        bool ready = body.TryGetProperty("ready", out JsonElement readyValue) && readyValue.ValueKind == JsonValueKind.True;
                        bool build = job.Manifest.ExpectedBuildVersion is null || (body.TryGetProperty("buildVersion", out JsonElement version) && string.Equals(version.GetString(), job.Manifest.ExpectedBuildVersion, StringComparison.Ordinal));
                        bool hasIdentity = body.TryGetProperty("processId", out JsonElement pid) && pid.TryGetInt32(out int number) && 0 < number && body.TryGetProperty("processStartedAt", out JsonElement startedAt) && startedAt.TryGetDateTimeOffset(out DateTimeOffset date);
                        if (samePeer && newInstance)
                        {
                            lastInstance = instanceValue.GetGuid();
                            if (hasIdentity)
                            {
                                ProcessIdentity reported = new(body.GetProperty("processId").GetInt32(), body.GetProperty("processStartedAt").GetDateTimeOffset());
                                if (startedProcess is not null && reported != startedProcess)
                                {
                                    ready = false;
                                }
                                else
                                {
                                    identity = reported;
                                }
                            }
                            if (ready && build && hasIdentity)
                            {
                                return new UpdateHealthResult(true, instanceValue.GetGuid(), identity);
                            }
                        }
                    }
                }
                catch (Exception exception) when (exception is JsonException or IOException or TaskCanceledException or InvalidOperationException)
                {
                    // 起動中の一時応答不能は全体の有限期限内だけ再観測します。
                }
                await Task.Delay(100, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
        return new UpdateHealthResult(false, lastInstance, identity);
    }
}
