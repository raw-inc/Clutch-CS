using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Local;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Health;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Runtime.Files;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Local;

/// <summary>installation外の単独実行プロセスで停止・入替・実起動確認を行います。</summary>
public static class LocalUpdateExecutor
{
    /// <summary>リリースの自己記述としてinstallationへ保存するファイル名です。</summary>
    public static string ManifestFileName => "release-manifest.json";

    /// <summary>中断状態を自動継続せず、固定した一回だけを適用します。</summary>
    /// <returns>実健康確認済みの結果または明示した失敗・結果不明です。</returns>
    public static async Task<UpdatePeerResult> ExecuteAsync(LocalUpdateJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        LocalUpdateJob owned = ContractJson.Copy(job);
        UpdateLayout layout = new(owned.Options, owned.OperationId);
        layout.CreateDirectories();
        string lockPath = Path.Combine(layout.StagingRoot, "installation.lock");
        layout.Validate(lockPath);
        using FileStream operationLock = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        UpdateJournal journal = new(layout, owned.PeerId);
        LocalUpdateRecord? existing = await journal.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!string.Equals(existing.Fingerprint, ContractJson.Hash(owned), StringComparison.Ordinal))
            {
                return Failure(owned, EOutcome.NotExecuted, "update.operationMismatch");
            }
            if (existing.Result.Phase is EUpdatePhase.Completed or EUpdatePhase.RolledBack or EUpdatePhase.Failed)
            {
                return existing.Result;
            }
            if (existing.Result.Phase != EUpdatePhase.Prepared)
            {
                return existing.Result with { Outcome = EOutcome.Unknown, Phase = EUpdatePhase.Interrupted, Reason = "update.manualRecoveryRequired" };
            }
        }
        bool stopStarted = false;
        try
        {
            ValidateJob(owned, layout);
            await UpdateRetention.TrimCoreAsync(owned.Options, owned.PeerId, owned.OperationId, cancellationToken).ConfigureAwait(false);
            await ReleaseVerifier.VerifyAsync(owned.Manifest, owned.PayloadRoot, cancellationToken).ConfigureAwait(false);
            ReleaseManifest previous = await ReadManifestAsync(layout.InstallationRoot, cancellationToken).ConfigureAwait(false);
            await ReleaseVerifier.VerifyAsync(previous, layout.InstallationRoot, cancellationToken).ConfigureAwait(false);
            if (!Directory.Exists(layout.CandidateRoot))
            {
                await ReleaseVerifier.CopyAsync(owned.Manifest, owned.PayloadRoot, layout.CandidateRoot, cancellationToken).ConfigureAwait(false);
            }
            layout.ProtectProgram(layout.CandidateRoot);
            await ReleaseVerifier.VerifyAsync(owned.Manifest, layout.CandidateRoot, cancellationToken).ConfigureAwait(false);
            await WriteManifestAsync(owned.Manifest, layout.CandidateRoot, cancellationToken).ConfigureAwait(false);
            await journal.WriteAsync(owned, new UpdatePeerResult(owned.OperationId, owned.PeerId, EUpdatePhase.Stopping, EOutcome.Unknown), cancellationToken).ConfigureAwait(false);
            stopStarted = true;
            EOutcome stopped = await UpdateProcessControl.StopAsync(owned, owned.PreviousProcess, cancellationToken).ConfigureAwait(false);
            if (stopped != EOutcome.Succeeded)
            {
                UpdatePeerResult failed = Failure(owned, stopped, "update.stopNotConfirmed") with { ReleaseId = previous.ReleaseId };
                await journal.WriteAsync(owned, failed, CancellationToken.None).ConfigureAwait(false);
                return failed;
            }
            await journal.WriteAsync(owned, new UpdatePeerResult(owned.OperationId, owned.PeerId, EUpdatePhase.Stopped, EOutcome.Unknown), cancellationToken).ConfigureAwait(false);
            layout.ProtectProgram(layout.InstallationRoot);
            layout.Validate(layout.InstallationRoot, true);
            layout.Validate(layout.BackupPath);
            layout.Validate(layout.CandidateRoot);
            if (Directory.Exists(layout.BackupPath))
            {
                return await RestoreWithoutReplacementAsync(owned, previous, layout, journal, cancellationToken).ConfigureAwait(false);
            }
            await UpdateDirectorySwitch.MoveAsync(layout.InstallationRoot, layout.BackupPath, layout, cancellationToken).ConfigureAwait(false);
            await journal.WriteAsync(owned, new UpdatePeerResult(owned.OperationId, owned.PeerId, EUpdatePhase.BackedUp, EOutcome.Unknown, BackupPath: layout.BackupPath), cancellationToken).ConfigureAwait(false);
            layout.Validate(layout.InstallationRoot, true);
            layout.Validate(layout.CandidateRoot);
            await UpdateDirectorySwitch.MoveAsync(layout.CandidateRoot, layout.InstallationRoot, layout, cancellationToken).ConfigureAwait(false);
            await journal.WriteAsync(owned, new UpdatePeerResult(owned.OperationId, owned.PeerId, EUpdatePhase.Installed, EOutcome.Unknown, BackupPath: layout.BackupPath), cancellationToken).ConfigureAwait(false);
            return await StartAndVerifyAsync(owned, previous, layout, journal, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            EOutcome outcome = EOutcome.NotExecuted;
            if (stopStarted)
            {
                outcome = EOutcome.Unknown;
            }
            return await RecordFailureAsync(owned, journal, outcome, "update.interrupted", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            EOutcome outcome = EOutcome.NotExecuted;
            if (stopStarted)
            {
                outcome = EOutcome.Unknown;
            }
            return await RecordFailureAsync(owned, journal, outcome, "update.operationUnconfirmed." + exception.GetType().Name + "." + exception.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture), CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>installed リリースの明示マニフェストを読みます。</summary>
    /// <returns>実ファイルの検証に使用する契約です。</returns>
    public static async Task<ReleaseManifest> ReadManifestAsync(string root, CancellationToken cancellationToken = default)
    {
        FileAccessPolicy policy = new(new FileSystemOptions([root]));
        string path = policy.ResolvePath(Path.Combine(root, ManifestFileName));
        string text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        ReleaseManifest manifest = JsonSerializer.Deserialize<ReleaseManifest>(text, ContractJson.Options) ?? throw new InvalidDataException("release manifestがありません。");
        ReleaseVerifier.Validate(manifest);
        return manifest;
    }

    /// <summary>検証済み候補へリリースの自己記述を保存します。</summary>
    /// <returns>ファイル保存の完了です。</returns>
    public static Task WriteManifestAsync(ReleaseManifest manifest, string root, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        FileAccessPolicy policy = new(new FileSystemOptions([root]));
        string path = policy.ResolvePath(Path.Combine(root, ManifestFileName));
        return File.WriteAllTextAsync(path, JsonSerializer.Serialize(manifest, ContractJson.Options), cancellationToken);
    }

    /// <summary>副作用前にplatform、管理root、起動引数を確認します。</summary>
    private static void ValidateJob(LocalUpdateJob job, UpdateLayout layout)
    {
        ReleaseVerifier.Validate(job.Manifest);
        bool platform = string.Equals(job.Manifest.Platform, RuntimeInformation.RuntimeIdentifier, StringComparison.Ordinal);
        bool manifestCollision = job.Manifest.Files.Any(file => string.Equals(file.Path, ManifestFileName, StringComparison.OrdinalIgnoreCase));
        if (!platform || manifestCollision || job.PreviousInstanceId == Guid.Empty || job.PreviousProcess.ProcessId < 1 || UpdateLayout.Contains(layout.InstallationRoot, AppContext.BaseDirectory) || !UpdateLayout.Contains(layout.StagingRoot, job.PayloadRoot))
        {
            throw new InvalidDataException("更新platform、自己入替回避またはpayload範囲の条件が不正です。");
        }
        layout.Validate(job.PayloadRoot);
        ArgumentNullException.ThrowIfNull(job.ApplicationArguments);
        for (int index = 0; index + 1 < job.ApplicationArguments.Count; index++)
        {
            string config = "--config";
            if (string.Equals(job.ApplicationArguments[index], config, StringComparison.Ordinal) && UpdateLayout.Contains(layout.InstallationRoot, job.ApplicationArguments[index + 1]))
            {
                throw new InvalidDataException("保持するconfigはinstallation root外に置いてください。");
            }
        }
    }

    /// <summary>新リリースの実起動を確認し、既知の健康確認失敗だけ旧版へ補償します。</summary>
    /// <returns>最終取得した起動結果です。</returns>
    private static async Task<UpdatePeerResult> StartAndVerifyAsync(LocalUpdateJob job, ReleaseManifest previous, UpdateLayout layout, UpdateJournal journal, CancellationToken cancellationToken = default)
    {
        await Installation.InstallationRecovery.SynchronizeCoreAsync(layout.InstallationRoot, job.Manifest, cancellationToken).ConfigureAwait(false);
        await journal.WriteAsync(job, new UpdatePeerResult(job.OperationId, job.PeerId, EUpdatePhase.Starting, EOutcome.Unknown, BackupPath: layout.BackupPath), cancellationToken).ConfigureAwait(false);
        using Process? process = await UpdateProcessControl.StartAsync(job, cancellationToken).ConfigureAwait(false);
        ProcessIdentity? identity = null;
        if (process is not null)
        {
            identity = ProcessOs.Identity(process);
        }
        UpdateHealthResult health = await UpdateHealthProbe.WaitAsync(job, identity, cancellationToken).ConfigureAwait(false);
        if (health.Ready)
        {
            await Installation.InstallationRecovery.RefreshCoreAsync(layout.InstallationRoot, job.Manifest, cancellationToken).ConfigureAwait(false);
            EUpdatePhase phase = EUpdatePhase.Completed;
            if (job.RollbackOf.HasValue)
            {
                phase = EUpdatePhase.RolledBack;
            }
            UpdatePeerResult completed = new(job.OperationId, job.PeerId, phase, EOutcome.Succeeded, job.Manifest.ReleaseId, layout.BackupPath, health.InstanceId);
            await journal.WriteAsync(job, completed, CancellationToken.None).ConfigureAwait(false);
            return completed;
        }
        if (health.Process is null)
        {
            return await RecordFailureAsync(job, journal, EOutcome.Unknown, "update.newProcessUnconfirmed", CancellationToken.None).ConfigureAwait(false);
        }
        await journal.WriteAsync(job, new UpdatePeerResult(job.OperationId, job.PeerId, EUpdatePhase.Restoring, EOutcome.Unknown, BackupPath: layout.BackupPath, InstanceId: health.InstanceId), cancellationToken).ConfigureAwait(false);
        EOutcome stopped;
        if (process is not null)
        {
            stopped = await UpdateProcessControl.StopAsync(job, health.Process, process, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            stopped = await UpdateProcessControl.StopAsync(job, health.Process, cancellationToken).ConfigureAwait(false);
        }
        if (stopped != EOutcome.Succeeded)
        {
            return await RecordFailureAsync(job, journal, EOutcome.Unknown, "update.rollbackStopUnconfirmed", CancellationToken.None).ConfigureAwait(false);
        }
        await ReleaseVerifier.VerifyAsync(previous, layout.BackupPath, cancellationToken).ConfigureAwait(false);
        string failedPath = Path.Combine(layout.OperationRoot, "failed-release");
        layout.Validate(failedPath);
        layout.Validate(layout.InstallationRoot, true);
        layout.Validate(layout.BackupPath);
        await UpdateDirectorySwitch.MoveAsync(layout.InstallationRoot, failedPath, layout, cancellationToken).ConfigureAwait(false);
        await UpdateDirectorySwitch.MoveAsync(layout.BackupPath, layout.InstallationRoot, layout, cancellationToken).ConfigureAwait(false);
        return await RestoreWithoutReplacementAsync(job, previous, layout, journal, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>旧リリースが残ることを検証して実際に再起動します。</summary>
    /// <returns>更新失敗と旧版復帰の結果です。</returns>
    private static async Task<UpdatePeerResult> RestoreWithoutReplacementAsync(LocalUpdateJob job, ReleaseManifest previous, UpdateLayout layout, UpdateJournal journal, CancellationToken cancellationToken = default)
    {
        await ReleaseVerifier.VerifyAsync(previous, layout.InstallationRoot, cancellationToken).ConfigureAwait(false);
        LocalUpdateJob restore = job with { Manifest = previous };
        await Installation.InstallationRecovery.SynchronizeCoreAsync(layout.InstallationRoot, previous, cancellationToken).ConfigureAwait(false);
        using Process? process = await UpdateProcessControl.StartAsync(restore, cancellationToken).ConfigureAwait(false);
        ProcessIdentity? identity = null;
        if (process is not null)
        {
            identity = ProcessOs.Identity(process);
        }
        UpdateHealthResult health = await UpdateHealthProbe.WaitAsync(restore, identity, cancellationToken).ConfigureAwait(false);
        UpdatePeerResult result = Failure(job, EOutcome.Unknown, "update.rollbackHealthUnconfirmed");
        if (health.Ready)
        {
            result = Failure(job, EOutcome.Failed, "update.failedOldReleaseRestored") with { ReleaseId = previous.ReleaseId, InstanceId = health.InstanceId };
        }
        await journal.WriteAsync(job, result, CancellationToken.None).ConfigureAwait(false);
        return result;
    }

    /// <summary>最後の保存に失敗した場合も成功を返しません。</summary>
    /// <returns>記録できた範囲の失敗です。</returns>
    private static async Task<UpdatePeerResult> RecordFailureAsync(LocalUpdateJob job, UpdateJournal journal, EOutcome outcome, string reason, CancellationToken cancellationToken = default)
    {
        UpdatePeerResult result = Failure(job, outcome, reason);
        try
        {
            await journal.WriteAsync(job, result, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return result with { Outcome = EOutcome.Unknown, Phase = EUpdatePhase.Interrupted, Reason = "update.journalUnavailable" };
        }
        return result;
    }

    /// <summary>受理済みを成功へ丸めない失敗結果を作ります。</summary>
    /// <returns>副作用確認範囲を保持する結果です。</returns>
    private static UpdatePeerResult Failure(LocalUpdateJob job, EOutcome outcome, string reason)
    {
        EUpdatePhase phase = EUpdatePhase.Failed;
        if (outcome == EOutcome.Unknown)
        {
            phase = EUpdatePhase.Interrupted;
        }
        return new UpdatePeerResult(job.OperationId, job.PeerId, phase, outcome, Reason: reason);
    }
}
