using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Fleet;
using System.Threading.Channels;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Client;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Fleet;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Updates.Fleet;

/// <summary>画面寿命から独立して一つの全体更新を所有し、再読込みへ進捗を配信します。</summary>
internal sealed class FleetUpdates(FleetUpdatePlanner planner, FleetUpdateStore store, IFleetUpdateRemote remote, ClientUpdateLauncher client) : Microsoft.Extensions.Hosting.BackgroundService
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Channel<FleetUpdateStatus> pending = Channel.CreateBounded<FleetUpdateStatus>(1);
    private readonly TaskCompletionSource initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool active;

    /// <summary>処理終了後の元操作を再照会し、確定した結果だけで未確認を解消します。</summary>
    /// <returns>再確認した元進捗です。</returns>
    internal async Task<FleetUpdateStatus> ReconcileAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await initialized.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (active)
            {
                throw new InvalidOperationException("An update is still running.");
            }
            FleetUpdateStatus current = await store.ReadAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new ArgumentException("Unknown update.", nameof(id));
            string completed = "Completed";
            if (current.Client is not null && current.Peers.All(peer => string.Equals(peer.State, completed, StringComparison.Ordinal)))
            {
                return current;
            }
            FleetUpdateStatus result = await new FleetUpdateRecovery(remote).ReconcileAsync(current, cancellationToken).ConfigureAwait(false);
            await store.SaveAsync(result, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>受付前に配布内容・対象・重複IDを確認し、永続化してから処理へ渡します。</summary>
    /// <returns>元要求の受付または保存済み結果です。</returns>
    internal async Task<FleetUpdateStatus> EnqueueAsync(FleetUpdateRequest request, CancellationToken cancellationToken = default)
    {
        await initialized.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            FleetUpdateStatus? existing = await store.ReadAsync(request.OperationId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!string.Equals(ContractJson.Hash(existing.Request), ContractJson.Hash(request), StringComparison.Ordinal))
                {
                    throw new ArgumentException("The original update request was changed.", nameof(request));
                }
                return existing;
            }
            if (active || (await store.ListAsync(cancellationToken).ConfigureAwait(false)).Any(status => !status.Completed || status.Outcome == EOutcome.Unknown))
            {
                throw new InvalidOperationException("Another update is active or needs inspection.");
            }
            planner.Validate(request);
            ClusterUpdateRequest validation = new(request.OperationId, request.Release.Manifest, request.Release.PayloadRoot, ["validation"],
                EUpdateFailurePolicy.KeepCompatibleMixed, Variants: request.Release.Variants);
            foreach (string platform in request.Release.Variants?.Select(variant => variant.Manifest.Platform) ?? [request.Release.Manifest.Platform])
            {
                ClusterUpdateRequest selected = ReleaseSelector.Select(validation, platform);
                await ReleaseVerifier.VerifyAsync(selected.Manifest, selected.PayloadRoot, cancellationToken).ConfigureAwait(false);
            }
            FleetUpdateRequest owned = ContractJson.Copy(request);
            await client.PrepareAsync(owned, cancellationToken).ConfigureAwait(false);
            ClientUpdateProgress? clientProgress = null;
            if (owned.Release.Client is not null)
            {
                clientProgress = new("Queued", false, EOutcome.Unknown, null, DateTimeOffset.UtcNow);
            }
            FleetUpdateStatus status = new(owned, false, EOutcome.Unknown,
                owned.Targets.Select(target => new FleetUpdatePeerProgress(target, "Queued", [])).ToArray(), DateTimeOffset.UtcNow,
                Client: clientProgress);
            await store.SaveAsync(status, cancellationToken).ConfigureAwait(false);
            active = true;
            if (!pending.Writer.TryWrite(status))
            {
                throw new InvalidOperationException("The update worker did not accept the saved operation.");
            }
            return status;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>ClientHost起動時に中断結果を復元し、受理した一回を完了まで所有します。</summary>
    /// <returns>Host終了までの更新処理です。</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await store.RecoverAsync(stoppingToken).ConfigureAwait(false);
        initialized.TrySetResult();
        await foreach (FleetUpdateStatus queued in pending.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            FleetUpdateStatus current = queued;
            try
            {
                FleetUpdateRunner runner = new(remote);
                FleetUpdateStatus result = await runner.RunAsync(queued, async (status, token) =>
                {
                    if (status.Request.Release.Client is not null && status.Completed && status.Outcome == EOutcome.Succeeded)
                    {
                        status = status with { Completed = false, Outcome = EOutcome.Unknown };
                    }
                    if (status.Request.Release.Client is not null && status.Completed)
                    {
                        status = status with { Client = new("Deferred", true, EOutcome.NotExecuted, null, DateTimeOffset.UtcNow,
                            "Core updates did not all succeed; ClientHost was not restarted.") };
                    }
                    await store.SaveAsync(status, token).ConfigureAwait(false);
                    current = status;
                }, stoppingToken).ConfigureAwait(false);
                if (result.Request.Release.Client is not null && result.Outcome == EOutcome.Succeeded)
                {
                    await client.StartAsync(result, async (status, token) =>
                    {
                        await store.SaveAsync(status, token).ConfigureAwait(false);
                        current = status;
                    }, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or OperationCanceledException)
            {
                Exception cause = exception.GetBaseException();
                string code = cause.GetType().Name + "." + cause.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
                await store.SaveAsync(current with { Completed = true, Outcome = EOutcome.Unknown, UpdatedAt = DateTimeOffset.UtcNow,
                    Reason = "The update worker stopped (" + code + "). Inspect the recorded operation IDs; dispatched updates were not repeated." }, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                active = false;
                gate.Release();
            }
        }
    }

    /// <summary>Hostが所有する受付排他を解放します。</summary>
    public override void Dispose()
    {
        pending.Writer.TryComplete();
        gate.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
