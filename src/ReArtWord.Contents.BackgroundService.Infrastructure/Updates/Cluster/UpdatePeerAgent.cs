using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Local;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Local;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Cluster;

/// <summary>一Peerで固定ジョブの準備と単独実行の一度だけの起動を扱います。</summary>
public sealed class UpdatePeerAgent
{
    private readonly UpdatesOptions options;
    private readonly string peerId;
    private readonly Guid instanceId;
    private readonly string[] arguments;
    private readonly string? launcher;

    /// <summary>現在の常駐実体と再起動引数を固定します。</summary>
    public UpdatePeerAgent(UpdatesOptions options, string peerId, Guid instanceId, IReadOnlyList<string> applicationArguments, string? launcherExecutablePath = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(peerId);
        ArgumentNullException.ThrowIfNull(applicationArguments);
        this.options = options;
        this.peerId = peerId;
        this.instanceId = instanceId;
        arguments = applicationArguments.ToArray();
        launcher = launcherExecutablePath;
    }

    /// <summary>未開始の固定ジョブと転送専用ディレクトリを準備します。</summary>
    /// <returns>旧リリースとこのPeerの転送先です。</returns>
    public async Task<UpdatePeerResult> PrepareAsync(ClusterUpdateRequest request, bool active, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.PeerIds.Contains(peerId, StringComparer.Ordinal))
        {
            throw new InvalidDataException("このPeerは固定更新対象に含まれていません。");
        }
        UpdateLayout layout = new(options, request.OperationId);
        layout.CreateDirectories();
        ReleaseManifest previous = await LocalUpdateExecutor.ReadManifestAsync(layout.InstallationRoot, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(request.Manifest.Platform, RuntimeInformation.RuntimeIdentifier, StringComparison.Ordinal))
        {
            return new UpdatePeerResult(request.OperationId, peerId, EUpdatePhase.Failed, EOutcome.NotExecuted, previous.ReleaseId, Reason: "update.platformMismatch");
        }
        bool mixed = request.Manifest.CompatibleReleaseIds.Contains(previous.ReleaseId, StringComparer.Ordinal) || string.Equals(request.Manifest.ReleaseId, previous.ReleaseId, StringComparison.Ordinal);
        if (active && (!request.AllowWhileActive || !request.RemainingOperationConfirmed || !mixed))
        {
            return new UpdatePeerResult(request.OperationId, peerId, EUpdatePhase.Failed, EOutcome.NotExecuted, previous.ReleaseId, Reason: "update.activeOperationNotAllowed");
        }
        if (File.Exists(layout.JobPath))
        {
            LocalUpdateJob existing = await ReadJobAsync(request.OperationId, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(ContractJson.Hash(existing.Manifest), ContractJson.Hash(request.Manifest), StringComparison.Ordinal))
            {
                throw new InvalidDataException("既存更新のreleaseを差し替えられません。");
            }
            return await ReadAsync(request.OperationId, cancellationToken).ConfigureAwait(false);
        }
        ReleaseVerifier.Validate(request.Manifest);
        layout.Validate(layout.PayloadRoot);
        Directory.CreateDirectory(layout.PayloadRoot);
        string? helper = await BundledUpdater.ResolveAsync(options, request.OperationId, cancellationToken).ConfigureAwait(false);
        LocalUpdateJob job = new(request.OperationId, peerId, instanceId, ProcessOs.CurrentIdentity(), request.Manifest, layout.PayloadRoot, options with { UpdaterExecutablePath = helper }, arguments, launcher);
        layout.Validate(layout.JobPath);
        using FileStream file = new(layout.JobPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(file, job, ContractJson.Options, cancellationToken).ConfigureAwait(false);
        await file.FlushAsync(cancellationToken).ConfigureAwait(false);
        return new UpdatePeerResult(request.OperationId, peerId, EUpdatePhase.Transferring, EOutcome.NotExecuted, previous.ReleaseId, InstanceId: instanceId, PayloadPath: layout.PayloadRoot, PreparedInstanceId: instanceId);
    }

    /// <summary>全転送完了後にチェックサムと実体を確認して準備を確定します。</summary>
    /// <returns>更新自体はまだ開始していない準備成功です。</returns>
    public async Task<UpdatePeerResult> SealAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        LocalUpdateJob job = await ReadJobAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (job.PreviousInstanceId != instanceId)
        {
            throw new InvalidDataException("準備対象の常駐instanceが変わりました。");
        }
        await ReleaseVerifier.VerifyAsync(job.Manifest, job.PayloadRoot, cancellationToken).ConfigureAwait(false);
        UpdateLayout layout = new(options, operationId);
        UpdateJournal journal = new(layout, peerId);
        LocalUpdateRecord? old = await journal.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (old is not null)
        {
            return old.Result;
        }
        ReleaseManifest previous = await LocalUpdateExecutor.ReadManifestAsync(layout.InstallationRoot, cancellationToken).ConfigureAwait(false);
        UpdatePeerResult prepared = new(operationId, peerId, EUpdatePhase.Prepared, EOutcome.Succeeded, previous.ReleaseId, InstanceId: instanceId, PayloadPath: layout.PayloadRoot, PreparedInstanceId: instanceId);
        await journal.WriteAsync(job, prepared, cancellationToken).ConfigureAwait(false);
        return prepared;
    }

    /// <summary>同じジョブを読むだけで副作用を再開しません。</summary>
    /// <returns>永続状態または書込実体喪失による結果不明です。</returns>
    public Task<UpdatePeerResult> ReadAsync(Guid operationId, CancellationToken cancellationToken = default)
        => ReadObservedAsync(operationId, ProcessOs.IsRunning, cancellationToken);

    /// <summary>永続記録と、その書込プロセスの観測を使って進行状態を確認します。</summary>
    /// <returns>観測できた更新状態です。</returns>
    internal async Task<UpdatePeerResult> ReadObservedAsync(Guid operationId, Func<ProcessIdentity, bool?> isRunning, CancellationToken cancellationToken = default)
    {
        UpdateLayout layout = new(options, operationId);
        layout.Validate(layout.JobPath);
        if (!File.Exists(layout.JobPath))
        {
            return new UpdatePeerResult(operationId, peerId, EUpdatePhase.Failed, EOutcome.NotExecuted, Reason: "update.operationNotFound");
        }
        UpdateJournal journal = new(layout, peerId);
        LocalUpdateRecord? record = await journal.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return new UpdatePeerResult(operationId, peerId, EUpdatePhase.Transferring, EOutcome.NotExecuted, PayloadPath: layout.PayloadRoot, PreparedInstanceId: instanceId);
        }
        if (!IsTerminal(record.Result.Phase) && isRunning(record.Writer) != true)
        {
            LocalUpdateRecord? latest = await journal.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (latest is not null)
            {
                // 終了の確認前に最終記録が保存されていた場合、その結果を中断へ置き換えません。
                if (IsTerminal(latest.Result.Phase) || latest.Writer != record.Writer)
                {
                    return latest.Result;
                }
                record = latest;
            }
            return record.Result with { Outcome = EOutcome.Unknown, Phase = EUpdatePhase.Interrupted, Reason = "update.writerInterrupted" };
        }
        return record.Result;
    }

    /// <summary>書込プロセスの終了後も、そのまま採用する確定済みの段階です。</summary>
    /// <returns>操作の最終記録であればtrueです。</returns>
    private static bool IsTerminal(EUpdatePhase phase) => phase is EUpdatePhase.Completed or EUpdatePhase.RolledBack or EUpdatePhase.Failed or EUpdatePhase.Interrupted;

    /// <summary>更新以外の操作を追加せず、固定ジョブを取得します。</summary>
    /// <returns>現在のジョブファイルです。</returns>
    public async Task<LocalUpdateJob> ReadJobAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        UpdateLayout layout = new(options, operationId);
        layout.Validate(layout.JobPath);
        string text = await File.ReadAllTextAsync(layout.JobPath, cancellationToken).ConfigureAwait(false);
        LocalUpdateJob job = JsonSerializer.Deserialize<LocalUpdateJob>(text, ContractJson.Options) ?? throw new InvalidDataException("更新jobがありません。");
        if (job.OperationId != operationId || !string.Equals(job.PeerId, peerId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("更新jobの実行先が一致しません。");
        }
        return job;
    }

    /// <summary>確認済み準備から単独実行を一度だけ起動します。</summary>
    /// <returns>起動要求の受理であり、更新成功ではありません。</returns>
    public async Task<UpdatePeerResult> DispatchAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        LocalUpdateJob job = await ReadJobAsync(operationId, cancellationToken).ConfigureAwait(false);
        UpdateLayout layout = new(options, operationId);
        UpdateJournal journal = new(layout, peerId);
        LocalUpdateRecord? current = await journal.ReadAsync(cancellationToken).ConfigureAwait(false);
        string? helper = job.Options.UpdaterExecutablePath;
        if (current?.Result.Phase != EUpdatePhase.Prepared || !string.Equals(current.Fingerprint, ContractJson.Hash(job), StringComparison.Ordinal) || job.PreviousInstanceId != instanceId || string.IsNullOrWhiteSpace(helper) || !Path.IsPathFullyQualified(helper) || !File.Exists(helper) || UpdateLayout.Contains(layout.InstallationRoot, helper))
        {
            return new UpdatePeerResult(operationId, peerId, EUpdatePhase.Failed, EOutcome.NotExecuted, Reason: "update.dispatchNotReady");
        }
        if (!await journal.ReserveDispatchAsync(job, cancellationToken).ConfigureAwait(false))
        {
            return await ReadAsync(operationId, cancellationToken).ConfigureAwait(false);
        }
        ProcessStartInfo start = new(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(helper)! };
        start.ArgumentList.Add("--job");
        start.ArgumentList.Add(layout.JobPath);
        using Process process = new() { StartInfo = start };
        if (!process.Start())
        {
            return new UpdatePeerResult(operationId, peerId, EUpdatePhase.Interrupted, EOutcome.Unknown, Reason: "update.helperStartUnconfirmed");
        }
        return new UpdatePeerResult(operationId, peerId, EUpdatePhase.Stopping, EOutcome.Unknown, Reason: "update.helperDispatched");
    }

    /// <summary>確認済みbackupから別の明示復元ジョブを準備します。</summary>
    /// <returns>二重作成を防いだ復元ジョブの準備結果です。</returns>
    public async Task<UpdatePeerResult> PrepareRollbackAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        UpdateLayout previousLayout = new(options, operationId);
        LocalUpdateRecord previous = await new UpdateJournal(previousLayout, peerId).ReadAsync(cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("旧更新を確認できません。");
        if (previous.Result.Phase != EUpdatePhase.Completed || previous.Result.Outcome != EOutcome.Succeeded || previous.Result.BackupPath is null)
        {
            throw new InvalidDataException("更新成功と旧版退避を確認した操作だけを復元できます。");
        }
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(operationId.ToString("N") + ":" + peerId + ":rollback"));
        Guid rollbackId = new(hash.AsSpan(0, 16));
        UpdateLayout layout = new(options, rollbackId);
        if (File.Exists(layout.JobPath))
        {
            return await ReadAsync(rollbackId, cancellationToken).ConfigureAwait(false);
        }
        layout.CreateDirectories();
        previousLayout.Validate(previous.Result.BackupPath);
        if (!UpdateLayout.Contains(previousLayout.BackupRoot, previous.Result.BackupPath))
        {
            throw new InvalidDataException("旧版退避が管理root外です。");
        }
        ReleaseManifest manifest = await LocalUpdateExecutor.ReadManifestAsync(previous.Result.BackupPath, cancellationToken).ConfigureAwait(false);
        await ReleaseVerifier.CopyAsync(manifest, previous.Result.BackupPath, layout.PayloadRoot, cancellationToken).ConfigureAwait(false);
        string? helper = await BundledUpdater.ResolveAsync(options, rollbackId, cancellationToken).ConfigureAwait(false);
        LocalUpdateJob job = new(rollbackId, peerId, instanceId, ProcessOs.CurrentIdentity(), manifest, layout.PayloadRoot, options with { UpdaterExecutablePath = helper }, arguments, launcher, RollbackOf: operationId);
        layout.Validate(layout.JobPath);
        using (FileStream file = new(layout.JobPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(file, job, ContractJson.Options, cancellationToken).ConfigureAwait(false);
            await file.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        UpdatePeerResult result = new(rollbackId, peerId, EUpdatePhase.Prepared, EOutcome.Succeeded, previous.Result.ReleaseId, InstanceId: instanceId, PayloadPath: layout.PayloadRoot, PreparedInstanceId: instanceId);
        await new UpdateJournal(layout, peerId).WriteAsync(job, result, cancellationToken).ConfigureAwait(false);
        return result;
    }
}
