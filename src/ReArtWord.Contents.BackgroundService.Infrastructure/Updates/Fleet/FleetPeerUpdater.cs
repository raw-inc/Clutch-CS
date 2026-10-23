using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Fleet;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Runtime.Updates;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Fleet;

/// <summary>ピア1件の更新を行い、更新後の起動とクラスターへの参加を確認します。</summary>
internal sealed class FleetPeerUpdater(IFleetUpdateRemote remote)
{
    /// <summary>対象を停止してもクラスターが稼働を維持できるか、所属情報と現在の稼働状態で判断します。</summary>
    /// <returns>保留する理由。更新できる場合はnull。</returns>
    internal async Task<string?> RemainingAsync(FleetUpdateRequest request, FleetUpdateTarget target, CancellationToken cancellationToken = default)
    {
        FleetUpdateTarget[] members = request.Targets.Where(peer => string.Equals(peer.ClusterId, target.ClusterId, StringComparison.Ordinal)).ToArray();
        IReadOnlyList<Contracts.Cluster.PeerInfo>? known = await remote.MembersAsync(target, cancellationToken).ConfigureAwait(false);
        if (known is null)
        {
            return "Cluster membership could not be confirmed. This peer was deferred.";
        }
        int population = known.Select(peer => peer.PeerId).Concat(members.Select(peer => peer.Peer.PeerId)).Distinct(StringComparer.Ordinal).Count();
        if (population == members.Length && population < 3 && members.Select(peer => peer.Peer.HostId).Distinct(StringComparer.Ordinal).Count() == 1)
        {
            return null;
        }

        // 更新対象外のピアも、クラスターの稼働を維持できるかどうかの判断に含めます。
        FleetUpdateTarget[] remaining = members.Concat(known.Select(peer => new FleetUpdateTarget(target.ClusterId, peer, target.Binding)))
            .DistinctBy(peer => peer.Peer.PeerId, StringComparer.Ordinal).Where(peer => !string.Equals(peer.Peer.PeerId, target.Peer.PeerId, StringComparison.Ordinal)).ToArray();
        FleetPeerObservation?[] observations = await Task.WhenAll(remaining.Select(peer => remote.ObserveAsync(peer, cancellationToken))).ConfigureAwait(false);
        int required = 1;
        if (3 <= population)
        {
            required = population / 2 + 1;
        }
        if (required <= observations.Count(peer => peer is { Ready: true }))
        {
            return null;
        }
        return "Too few Ready peers would remain. This peer was deferred.";
    }

    /// <summary>更新が未実行、または旧版への復帰を確認できた場合に限り、上限回数まで再試行します。</summary>
    /// <returns>対象ピアの更新結果。</returns>
    // 抑制理由: factoryが返す一試行のtransportはfinallyでIAsyncDisposableを確認して解放します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW141", Justification = "一試行のtransportはfinallyで非同期解放します。")]
    internal async Task<FleetUpdatePeerProgress> UpdateAsync(FleetUpdateRequest request, FleetUpdatePeerProgress progress,
        Func<FleetUpdatePeerProgress, Task<bool>> save, CancellationToken cancellationToken = default)
    {
        FleetUpdateTarget target = progress.Target;
        for (int attempt = 1; attempt <= request.Release.MaximumAttempts; attempt++)
        {
            if (1 < attempt)
            {
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            }
            FleetPeerObservation? before = await remote.ObserveAsync(target, cancellationToken).ConfigureAwait(false);
            if (before is not { Ready: true } || before.Peer.InstanceId != target.Peer.InstanceId)
            {
                return progress with { State = "Deferred", Reason = "The original peer is not Ready or its instance changed." };
            }
            Guid operation = Guid.NewGuid();
            progress = progress with { State = "Preparing", Attempts = progress.Attempts.Append(new FleetUpdateAttempt(operation, PreviousInstanceId: target.Peer.InstanceId)).ToArray(), Reason = null };
            if (!await save(progress).ConfigureAwait(false))
            {
                return progress with { State = "Deferred", Attempts = progress.Attempts.Take(progress.Attempts.Count - 1).ToArray(), Reason = "Further updates stopped after an unconfirmed result." };
            }
            ClusterUpdateRequest input = new(operation, request.Release.Manifest, request.Release.PayloadRoot, [target.Peer.PeerId],
                EUpdateFailurePolicy.KeepCompatibleMixed, AllowWhileActive: request.Release.AllowWhileActive, RemainingOperationConfirmed: true,
                PeerTimeoutMilliseconds: request.Release.PeerTimeoutMilliseconds, Variants: request.Release.Variants);
            IUpdatePeerTransport transport = remote.Create(input, target);
            bool dispatched = false;
            string step = "platform";
            try
            {
                string platform = await transport.GetPlatformAsync(target.Peer.PeerId, cancellationToken).ConfigureAwait(false);
                ClusterUpdateRequest selected = ReleaseSelector.Select(input, platform);
                step = "stage";
                UpdatePeerResult prepared = await transport.StageAsync(selected, target.Peer.PeerId, cancellationToken).ConfigureAwait(false);
                progress = WithResult(progress, prepared);
                if (prepared.Phase != EUpdatePhase.Prepared || prepared.Outcome != EOutcome.Succeeded || prepared.PreparedInstanceId != target.Peer.InstanceId)
                {
                    progress = progress with { State = "Failed", Reason = prepared.Reason ?? "Release preparation was not confirmed." };
                    await save(progress).ConfigureAwait(false);
                    continue;
                }
                bool compatible = string.Equals(prepared.ReleaseId, selected.Manifest.ReleaseId, StringComparison.Ordinal)
                    || selected.Manifest.CompatibleReleaseIds.Contains(prepared.ReleaseId ?? string.Empty, StringComparer.Ordinal);
                if (!compatible)
                {
                    return progress with { State = "Deferred", Reason = "The release does not declare compatibility with the running release." };
                }
                string? remaining = await RemainingAsync(request, target, cancellationToken).ConfigureAwait(false);
                if (remaining is not null)
                {
                    return progress with { State = "Deferred", Reason = remaining };
                }
                progress = progress with { State = "Updating" };
                if (!await save(progress).ConfigureAwait(false))
                {
                    return progress with { State = "Deferred", Reason = "Further updates stopped after an unconfirmed result." };
                }
                dispatched = true;
                step = "dispatch";
                UpdatePeerResult result = await transport.DispatchAsync(operation, target.Peer.PeerId, cancellationToken).ConfigureAwait(false);
                step = "confirm";
                result = await WaitAsync(transport, result, input.PeerTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
                progress = WithResult(progress, result);
                await save(progress).ConfigureAwait(false);
                bool succeeded = result.Phase == EUpdatePhase.Completed && result.Outcome == EOutcome.Succeeded
                    && result.InstanceId.HasValue && result.InstanceId != target.Peer.InstanceId;
                string missingOperation = "update.operationNotFound";
                bool retryable = (result.Phase == EUpdatePhase.Failed && result.Outcome == EOutcome.NotExecuted && !string.Equals(result.Reason, missingOperation, StringComparison.Ordinal))
                    || FleetUpdateResult.IsRestored(result, target.Peer.InstanceId);
                if (!succeeded && !retryable)
                {
                    return progress with { State = "Unconfirmed", Reason = result.Reason ?? "The update outcome is unconfirmed. No update was repeated." };
                }
                step = "release";
                await ReleaseAsync(transport, operation, target.Peer.PeerId, cancellationToken).ConfigureAwait(false);
                Guid expected = result.InstanceId ?? target.Peer.InstanceId;
                string? expectedVersion = before.BuildVersion;
                if (succeeded)
                {
                    expectedVersion = selected.Manifest.ExpectedBuildVersion;
                }
                step = "ready";
                FleetPeerObservation? restored = await WaitReadyAsync(target, expected, expectedVersion, input.PeerTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
                if (restored is null)
                {
                    return progress with { State = "Unconfirmed", Reason = "The expected peer did not return to Ready. Further updates were stopped." };
                }
                if (succeeded)
                {
                    return progress with { State = "Completed", Reason = null };
                }
                target = target with { Peer = restored.Peer };
                progress = progress with { State = "Retrying", Reason = result.Reason };
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress = WithProblem(progress, step, exception);
                if (dispatched)
                {
                    return progress with { State = "Unconfirmed", Reason = "Update or recovery could not be confirmed. No update was repeated." };
                }
                progress = progress with { State = "Retrying", Reason = "Release preparation failed before dispatch." };
            }
            finally
            {
                if (transport is IAsyncDisposable disposable)
                {
                    await disposable.DisposeAsync().ConfigureAwait(false);
                }
            }
            await save(progress).ConfigureAwait(false);
        }
        return progress with { State = "Failed", Reason = progress.Reason ?? "The maximum number of attempts was reached." };
    }

    /// <summary>受付済みの更新結果を照会します。応答がなくても更新要求は再送しません。</summary>
    /// <returns>完了した更新の結果。確認できない場合は結果不明。</returns>
    private static async Task<UpdatePeerResult> WaitAsync(IUpdatePeerTransport transport, UpdatePeerResult result, int milliseconds, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(milliseconds);
        try
        {
            while (result.Phase is not (EUpdatePhase.Completed or EUpdatePhase.RolledBack or EUpdatePhase.Failed or EUpdatePhase.Interrupted))
            {
                await Task.Delay(500, timeout.Token).ConfigureAwait(false);
                result = await transport.ReadAsync(result.OperationId, result.PeerId, timeout.Token).ConfigureAwait(false);
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return result with { Phase = EUpdatePhase.Interrupted, Outcome = EOutcome.Unknown, Reason = "Update confirmation timed out." };
        }
    }

    /// <summary>この更新が取得した保守保留を解除します。通信エラーの場合は最大3回試行します。</summary>
    /// <returns>保守保留の解除。</returns>
    private static async Task ReleaseAsync(IUpdatePeerTransport transport, Guid operation, string peer, CancellationToken cancellationToken = default)
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await transport.ReleaseAsync(operation, peer, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (IOException) when (attempt < 3)
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
        }
        throw new IOException("Maintenance release was not confirmed.");
    }

    /// <summary>起動後のInstanceId、バージョン、Ready状態、保守保留の解除を確認します。</summary>
    /// <returns>確認できた稼働状態。期限内に確認できない場合はnull。</returns>
    private async Task<FleetPeerObservation?> WaitReadyAsync(FleetUpdateTarget target, Guid expected, string? version, int milliseconds, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(milliseconds);
        try
        {
            while (!timeout.IsCancellationRequested)
            {
                FleetPeerObservation? observed = await remote.ObserveAsync(target, timeout.Token).ConfigureAwait(false);
                if (observed is { Ready: true } && observed.Peer.InstanceId == expected
                    && (version is null || string.Equals(version, observed.BuildVersion, StringComparison.Ordinal)))
                {
                    return observed;
                }
                await Task.Delay(500, timeout.Token).ConfigureAwait(false);
            }
            return null;
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    /// <summary>操作IDと対象ピアを照合し、該当する試行の結果を記録します。</summary>
    /// <returns>結果を反映した進捗情報。</returns>
    private static FleetUpdatePeerProgress WithResult(FleetUpdatePeerProgress progress, UpdatePeerResult result)
    {
        FleetUpdateAttempt[] attempts = progress.Attempts.ToArray();
        if (attempts[^1].OperationId != result.OperationId || !string.Equals(progress.Target.Peer.PeerId, result.PeerId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The updater replied for another operation or peer.");
        }
        attempts[^1] = attempts[^1] with { Result = result };
        return progress with { Attempts = attempts };
    }

    /// <summary>失敗した工程とエラーコードを記録します。</summary>
    /// <returns>エラー情報を反映した進捗情報。</returns>
    private static FleetUpdatePeerProgress WithProblem(FleetUpdatePeerProgress progress, string step, Exception exception)
    {
        string code = exception.GetType().Name + "." + exception.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
        if (exception is UpdateTransportException transport)
        {
            code = transport.Code;
        }
        FleetUpdateAttempt[] attempts = progress.Attempts.ToArray();
        attempts[^1] = attempts[^1] with { Problem = step + ": " + code };
        return progress with { Attempts = attempts };
    }
}
