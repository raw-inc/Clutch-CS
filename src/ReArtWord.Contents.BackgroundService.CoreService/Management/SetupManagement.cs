using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting.Composition;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;

namespace ReArtWord.Contents.BackgroundService.CoreService.Management;

/// <summary>自分の配置の保守だけを許可し、helper成立後にHostを正常終了します。</summary>
internal sealed class SetupManagement : Microsoft.Extensions.Hosting.BackgroundService
{
    private readonly ServiceRuntime runtime;
    private readonly IHostApplicationLifetime lifetime;
    private readonly SemaphoreSlim gate = new(1, 1);
    private SetupJob? pending;

    /// <summary>配置記録、明示保守開始と既存Hostの寿命を接続します。</summary>
    public SetupManagement(ServiceRuntime runtime, IHostApplicationLifetime lifetime)
    {
        this.runtime = runtime;
        this.lifetime = lifetime;
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
        SetupJob? job = pending;
        if (job is null)
        {
            return;
        }
        string signal = Path.Combine(SetupWorkspace.DirectoryFor(job.OperationId), "stop.request");
        string resultPath = Path.Combine(SetupWorkspace.DirectoryFor(job.OperationId), "result.json");
        if (!File.Exists(signal) && File.Exists(resultPath))
        {
            SetupResult? result = System.Text.Json.JsonSerializer.Deserialize<SetupResult>(await File.ReadAllTextAsync(resultPath, cancellationToken).ConfigureAwait(false), ContractJson.Options);
            if (result?.OperationId == job.OperationId && result.Finished)
            {
                await runtime.Maintenance.ExitAsync(job.OperationId, cancellationToken).ConfigureAwait(false);
                await runtime.Sources.ReconcileAsync(cancellationToken).ConfigureAwait(false);
                pending = null;
            }
            return;
        }
        if (File.Exists(signal) && string.Equals(await File.ReadAllTextAsync(signal, cancellationToken).ConfigureAwait(false), ContractJson.Hash(job), StringComparison.Ordinal))
        {
            // 再配置後の通常参加を保守保留だけで阻害しないよう、停止直前に所有保留を解放します。
            await runtime.Maintenance.ExitAsync(job.OperationId, cancellationToken).ConfigureAwait(false);
            lifetime.StopApplication();
        }
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
