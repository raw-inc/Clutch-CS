using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting.Composition;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;

namespace ReArtWord.Contents.BackgroundService.CoreService.Management;

/// <summary>自分の配置の保守だけを許可し、helper成立後にHostを正常終了します。</summary>
internal sealed class SetupManagement : Microsoft.Extensions.Hosting.BackgroundService
{
    private readonly ServiceRuntime runtime;
    private readonly IHostApplicationLifetime lifetime;
    private readonly IWorkflowLog log;
    private readonly SemaphoreSlim gate = new(1, 1);
    private SetupJob? pending;
    private string? lastProblem;

    /// <summary>配置記録、明示保守開始と既存Hostの寿命を接続します。</summary>
    public SetupManagement(ServiceRuntime runtime, IHostApplicationLifetime lifetime, IWorkflowLog log)
    {
        this.runtime = runtime;
        this.lifetime = lifetime;
        this.log = log;
    }

    /// <summary>この配置の照会と保守操作を管理経路へ登録します。</summary>
    public void Register(ManagementRegistry registry)
    {
        registry.Read("setup.info", async (_, token) => await ReadAsync(token).ConfigureAwait(false));
        registry.Write("setup.start", async (body, token) => await StartAsync(ManagementRegistry.Body<RemoteSetupRequest>(body), token).ConfigureAwait(false));
    }

    /// <summary>配置記録と現在Peerの一致を検証します。</summary>
    /// <returns>このPeerの導入記録です。</returns>
    internal async Task<InstallationRecord> ReadAsync(CancellationToken cancellationToken = default)
    {
        string installation = runtime.Configuration.Updates.InstallationRoot ?? throw new InvalidDataException("This peer has no managed installation.");
        InstallationRecord record = await InstallationFiles.ReadAsync(Path.GetDirectoryName(installation)!, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(record.Settings.ClusterId, runtime.Configuration.Cluster.Id, StringComparison.Ordinal) || !string.Equals(record.Settings.PeerId, runtime.Cluster.LocalPeerId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The installation identity does not match this peer.");
        }
        return record;
    }

    /// <summary>固定対象を停止準備し、実行を独立helperへ引き継ぎます。</summary>
    /// <returns>受理または未実行の理由です。</returns>
    private async Task<SetupResult> StartAsync(RemoteSetupRequest request, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            InstallationRecord record = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (request.OperationId == Guid.Empty || request.InstallationId != record.InstallationId || request.Action == ESetupAction.Install || !Enum.IsDefined(request.Action))
            {
                throw new InvalidDataException("Select this installation and a supported maintenance action.");
            }
            if (pending is not null)
            {
                return new(request.OperationId, EOutcome.NotExecuted, "busy", "A maintenance operation is already in progress.", record.Settings.Root);
            }
            bool survivor = runtime.Cluster.GetPeers().Any(peer => peer.Status == EPeerStatus.Ready && !string.Equals(peer.PeerId, runtime.Cluster.LocalPeerId, StringComparison.Ordinal));
            if (request.Action == ESetupAction.Reinstall && !request.KeepDatabase && (!request.SurvivorConfirmed || !survivor))
            {
                return new(request.OperationId, EOutcome.NotExecuted, "survivor", "No other Ready peer can provide the database.", record.Settings.Root);
            }
            SetupJob job = new(request.OperationId, request.Action, record.Settings, record.InstallationId, request.KeepDatabase, request.KeepConfiguration,
                new(ServiceIdentity.Current.ProcessId, ServiceIdentity.Current.ProcessStartedAt), true, survivor && request.SurvivorConfirmed);
            string helper = await SetupWorkspace.PrepareBundledAsync(job, Path.Combine(record.Settings.Root, "core"), Path.Combine(record.Settings.Root, "updater"), cancellationToken).ConfigureAwait(false);
            string maintenanceKind = "setup";
            if (!await runtime.Maintenance.EnterAsync(request.OperationId, maintenanceKind, cancellationToken).ConfigureAwait(false))
            {
                return new(request.OperationId, EOutcome.NotExecuted, "busy", "Another operation owns maintenance.", record.Settings.Root);
            }
            await runtime.Sources.ReconcileAsync(cancellationToken).ConfigureAwait(false);
            if (runtime.Executor.Running.Count != 0)
            {
                await runtime.Maintenance.ExitAsync(request.OperationId, cancellationToken).ConfigureAwait(false);
                return new(request.OperationId, EOutcome.NotExecuted, "busy", "Wait for running workflows to finish before maintenance.", record.Settings.Root);
            }
            try
            {
                using Process process = SetupWorkspace.Launch(helper, Path.Combine(SetupWorkspace.DirectoryFor(job.OperationId), "job.json"), false);
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
            {
                await runtime.Maintenance.ExitAsync(request.OperationId, cancellationToken).ConfigureAwait(false);
                await runtime.Sources.ReconcileAsync(cancellationToken).ConfigureAwait(false);
                return new(request.OperationId, EOutcome.NotExecuted, "helper", "The maintenance helper could not start. The peer is still running.", record.Settings.Root);
            }
            pending = job;
            return new(request.OperationId, EOutcome.Unknown, "accepted", "Waiting for the independent maintenance helper.", record.Settings.Root, false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>helperの固定job確認後だけ、Hostを正常終了します。</summary>
    /// <returns>確認完了です。</returns>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (pending is { } job && await RefreshAsync(job, cancellationToken).ConfigureAwait(false))
            {
                pending = null;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>一操作の読取り失敗を隔離し、確認不能の間は所有する保守保留を維持します。</summary>
    /// <returns>停止通知または最終結果を確認し、この操作の監視が完了したかです。</returns>
    internal async Task<bool> RefreshAsync(SetupJob job, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string directory = SetupWorkspace.DirectoryFor(job.OperationId);
        string signal = Path.Combine(directory, "stop.request");
        StringComparison pathComparison = StringComparison.Ordinal;
        if (OperatingSystem.IsWindows())
        {
            pathComparison = StringComparison.OrdinalIgnoreCase;
        }
        try
        {
            string resultPath = Path.Combine(directory, "result.json");
            string? resultText = await ReadTextAsync(resultPath, cancellationToken).ConfigureAwait(false);
            if (resultText is not null)
            {
                SetupResult? result = System.Text.Json.JsonSerializer.Deserialize<SetupResult>(resultText, ContractJson.Options);
                if (result is null || result.OperationId != job.OperationId || !Enum.IsDefined(result.Outcome)
                    || !string.Equals(InstallationFiles.Root(result.Root), InstallationFiles.Root(job.Settings.Root), pathComparison))
                {
                    throw new InvalidDataException("The setup result does not belong to this operation.");
                }
                if (result.Finished)
                {
                    // 終了済みhelperの停止要求ではHostを止めません。結果不明は自動で保留解除しません。
                    if (result.Outcome == EOutcome.Unknown)
                    {
                        await ReportAsync(job, "setup.resultUnconfirmed", result.Detail, cancellationToken).ConfigureAwait(false);
                        return false;
                    }
                    if (!await ReleaseAsync(job, cancellationToken).ConfigureAwait(false))
                    {
                        return false;
                    }
                    await runtime.Sources.ReconcileAsync(cancellationToken).ConfigureAwait(false);
                    lastProblem = null;
                    return true;
                }
            }
            string? signalText = await ReadTextAsync(signal, cancellationToken).ConfigureAwait(false);
            if (signalText is not null)
            {
                if (!string.Equals(signalText, ContractJson.Hash(job), StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The setup stop request does not match this operation.");
                }
                if (!await ReleaseAsync(job, cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }

                // 再配置後の通常参加を保守保留だけで阻害しないよう、所有保留の解除後に停止します。
                lifetime.StopApplication();
                lastProblem = null;
                return true;
            }
            lastProblem = null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException && (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested))
        {
            await ReportAsync(job, "setup.monitorUnconfirmed", exception.GetType().Name + ": " + exception.Message, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>別操作が所有する保留を解除せず、停止も要求しません。</summary>
    /// <returns>この操作による保留の解除を確認できたかです。</returns>
    private async Task<bool> ReleaseAsync(SetupJob job, CancellationToken cancellationToken = default)
    {
        if (await runtime.Maintenance.ExitAsync(job.OperationId, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }
        await ReportAsync(job, "setup.maintenanceOwnerChanged", "The maintenance owner could not be confirmed.", cancellationToken).ConfigureAwait(false);
        return false;
    }

    /// <summary>書込み側の原子的置換を妨げず、公開された一つの本文を読みます。</summary>
    /// <returns>読み取った本文、未公開ならnullです。読取り失敗は呼出元へ伝えます。</returns>
    private static async Task<string?> ReadTextAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new(stream);
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>同じ障害を毎回記録せず、診断の失敗もCoreの寿命へ波及させません。</summary>
    /// <returns>診断の受付完了です。</returns>
    private async Task ReportAsync(SetupJob job, string reason, string detail, CancellationToken cancellationToken = default)
    {
        string problem = job.OperationId.ToString("N") + ":" + reason + ":" + detail;
        if (string.Equals(lastProblem, problem, StringComparison.Ordinal))
        {
            return;
        }
        lastProblem = problem;
        await WorkflowLogBoundary.WriteAsync(log, new WorkflowLogEntry(Guid.NewGuid(), DateTimeOffset.UtcNow, "WARN", runtime.Cluster.LocalPeerId,
            "セットアップの状態確認を保留し、次の監視で再確認します。", CausationId: job.OperationId, Data: ContractJson.ToValue(new { reason, detail })), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>固定した保守要求の受付結果を、Hostの停止まで確認します。</summary>
    /// <returns>監視終了です。</returns>
    protected override async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        using PeriodicTimer timer = new(InstallationDefaults.PollInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>管理受付用同期資源を解放します。</summary>
    public override void Dispose()
    {
        gate.Dispose();
        base.Dispose();
    }
}
