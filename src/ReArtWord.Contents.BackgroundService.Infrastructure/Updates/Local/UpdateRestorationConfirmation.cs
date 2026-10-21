using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Local;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Health;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Local;

/// <summary>期限後の旧版復帰を、停止や入替を再実行せず明示的に確認します。</summary>
public static class UpdateRestorationConfirmation
{
    /// <summary>元ジョブ、指定した旧版、現在の実体を照合して確認結果だけを保存します。</summary>
    /// <returns>確定した旧版復帰、または変更しない元結果です。</returns>
    public static async Task<UpdatePeerResult> ConfirmAsync(LocalUpdateJob job, string releaseId, string buildVersion, Guid instanceId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(buildVersion);
        if (instanceId == Guid.Empty || instanceId == job.PreviousInstanceId)
        {
            throw new ArgumentException("復帰した別実体を明示してください。", nameof(instanceId));
        }
        UpdateLayout layout = new(job.Options, job.OperationId);
        string lockPath = Path.Combine(layout.StagingRoot, "installation.lock");
        layout.Validate(lockPath);
        using FileStream operationLock = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        UpdateJournal journal = new(layout, job.PeerId);
        LocalUpdateRecord record = await journal.ReadAsync(cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("元の更新記録がありません。");
        if (!string.Equals(record.Fingerprint, ContractJson.Hash(job), StringComparison.Ordinal)
            || record.Result.OperationId != job.OperationId || !string.Equals(record.Result.PeerId, job.PeerId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("元の更新操作と入力が一致しません。");
        }
        string unconfirmed = "update.rollbackHealthUnconfirmed";
        if (record.Result.Phase != EUpdatePhase.Interrupted || record.Result.Outcome != EOutcome.Unknown
            || !string.Equals(record.Result.Reason, unconfirmed, StringComparison.Ordinal))
        {
            return record.Result;
        }
        ReleaseManifest installed = await LocalUpdateExecutor.ReadManifestAsync(layout.InstallationRoot, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(installed.ReleaseId, releaseId, StringComparison.Ordinal)
            || !string.Equals(installed.ExpectedBuildVersion, buildVersion, StringComparison.Ordinal)
            || !job.Manifest.CompatibleReleaseIds.Contains(releaseId, StringComparer.Ordinal))
        {
            throw new InvalidDataException("指定した互換旧版と実際のinstallationが一致しません。");
        }
        await ReleaseVerifier.VerifyAsync(installed, layout.InstallationRoot, cancellationToken).ConfigureAwait(false);
        UpdateHealthResult health = await UpdateHealthProbe.WaitAsync(job with { Manifest = installed }, null, cancellationToken).ConfigureAwait(false);
        if (!health.Ready || health.InstanceId != instanceId)
        {
            return record.Result;
        }
        UpdatePeerResult result = record.Result with { Phase = EUpdatePhase.Failed, Outcome = EOutcome.Failed,
            ReleaseId = installed.ReleaseId, InstanceId = instanceId, Reason = "update.failedOldReleaseRestored" };
        await journal.WriteAsync(job, result, cancellationToken).ConfigureAwait(false);
        return result;
    }
}
