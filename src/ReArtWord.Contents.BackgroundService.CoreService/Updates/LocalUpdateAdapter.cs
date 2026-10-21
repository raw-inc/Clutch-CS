using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Local;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting.Composition;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Events;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Health;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Local;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Scheduling;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Streams;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.CoreService.Updates;

/// <summary>更新準備と通常実行の永続保留をこのHostへ接続します。</summary>
internal sealed class LocalUpdateAdapter
{
    private readonly ServiceRuntime runtime;
    private readonly IHostApplicationLifetime host;
    private readonly string[] arguments;
    private readonly string? launcher;
    private readonly ConcurrentDictionary<Guid, ManagementCommand> pendingStops = [];

    /// <summary>実Hostの起動引数と設定を単独実行ジョブへ固定します。</summary>
    public LocalUpdateAdapter(ServiceRuntime runtime, UpdatesOptions options, IHostApplicationLifetime host)
    {
        this.runtime = runtime;
        this.host = host;
        Events = new(runtime.Repository, runtime.Events, runtime.Cluster.LocalPeerId);
        Options = options;
        arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
        for (int index = 0; index + 1 < arguments.Length; index++)
        {
            string config = "--config";
            if (string.Equals(arguments[index], config, StringComparison.Ordinal))
            {
                arguments[index + 1] = Path.GetFullPath(arguments[index + 1]);
            }
        }
        launcher = null;
        string dotnet = "dotnet";
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), dotnet, StringComparison.OrdinalIgnoreCase))
        {
            launcher = Environment.ProcessPath;
        }
        Agent = new UpdatePeerAgent(options, runtime.Cluster.LocalPeerId, runtime.Cluster.LocalInstanceId, arguments, launcher);
        Stream = new UpdateStreamReceiver(() => Agent, () => Options,
            peerId => runtime.Cluster.GetPeers().FirstOrDefault(peer => string.Equals(peer.PeerId, peerId, StringComparison.Ordinal))?.Address,
            runtime.Cluster.LocalInstanceId);
        Launcher = new(runtime, this);
        Schedules = new(runtime.Repository, runtime.Cluster.LocalPeerId, Launcher);
        Scheduler = new(Schedules, Launcher, () => runtime.Status == Contracts.Cluster.EPeerStatus.Ready && !runtime.Maintenance.IsClosed);
    }

    /// <summary>即時開始と予約開始で共用するUpdater入口です。</summary>
    public ClusterUpdateLauncher Launcher
    {
        get;
    }

    /// <summary>この起点Peerが所有する予約の保存です。</summary>
    public UpdateScheduleStore Schedules
    {
        get;
    }

    /// <summary>Core寿命に従う予約監視です。</summary>
    public UpdateScheduler Scheduler
    {
        get;
    }

    /// <summary>Host寿命に従う連続ペイロード受信です。</summary>
    public UpdateStreamReceiver Stream
    {
        get;
    }

    /// <summary>独立Updaterの保存した事象を共通Hookへ渡します。</summary>
    public UpdateEventPump Events
    {
        get;
    }

    /// <summary>このPeerの更新用root設定です。</summary>
    public UpdatesOptions Options
    {
        get; private set;
    }

    /// <summary>実行や転送を一度ずつ扱う共通agentです。</summary>
    public UpdatePeerAgent Agent
    {
        get; private set;
    }

    /// <summary>動作中の通常処理に対する更新許可を事前に確認します。</summary>
    /// <returns>固定した転送準備です。</returns>
    public Task<UpdatePeerResult> PrepareAsync(ClusterUpdateRequest request, CancellationToken cancellationToken = default)
    {
        bool active = runtime.Executor.Running.Count != 0 || runtime.Sources.GetSources().Any(source => source.Result is null);
        return Agent.PrepareAsync(request, active, cancellationToken);
    }

    /// <summary>開始済みをdrainしてから、この一回のhelperだけを起動します。</summary>
    /// <returns>単独実行の受理または明示した未開始です。</returns>
    public async Task<UpdatePeerResult> DispatchAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        LocalUpdateJob job = await Agent.ReadJobAsync(operationId, cancellationToken).ConfigureAwait(false);
        Guid owner = job.RollbackOf ?? operationId;
        bool entered = await runtime.Maintenance.EnterAsync(owner, "update", cancellationToken).ConfigureAwait(false);
        if (!entered)
        {
            return new UpdatePeerResult(operationId, runtime.Cluster.LocalPeerId, EUpdatePhase.Failed, EOutcome.NotExecuted, Reason: "update.maintenanceOwnedElsewhere");
        }
        await runtime.Sources.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        using CancellationTokenSource deadline = new(TimeSpan.FromMilliseconds(Options.StopTimeoutMilliseconds));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
        try
        {
            while (runtime.Executor.Running.Count != 0)
            {
                await Task.Delay(50, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return new UpdatePeerResult(operationId, runtime.Cluster.LocalPeerId, EUpdatePhase.Failed, EOutcome.NotExecuted, Reason: "update.drainNotCompleted");
        }
        return await Agent.DispatchAsync(operationId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>旧版復元も元操作のmaintenance所有者のまま行います。</summary>
    /// <returns>明示復元の受理または確認済み結果です。</returns>
    public async Task<UpdatePeerResult> RollbackAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        UpdatePeerResult prepared = await Agent.PrepareRollbackAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (prepared.Phase != EUpdatePhase.Prepared)
        {
            return prepared;
        }
        return await DispatchAsync(prepared.OperationId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>互換性・最終結果を確認した同じ操作だけが通常処理を再開します。</summary>
    /// <returns>保留解除の確認です。</returns>
    public async Task<bool> ReleaseAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        bool released = await runtime.Maintenance.ExitAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (released)
        {
            await runtime.Sources.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        }
        return released;
    }

    /// <summary>復元不能Peerを再起動後も通常参加させません。</summary>
    /// <returns>明示除外の保存です。</returns>
    public async Task<bool> ExcludeAsync(CancellationToken cancellationToken = default)
    {
        await runtime.Cluster.DetachAsync(cancellationToken).ConfigureAwait(false);
        await runtime.Sources.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>クラスタ停止方針を通常実行の永続保留として適用します。</summary>
    /// <returns>同じ操作による保留確認です。</returns>
    public async Task<bool> HoldAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        bool entered = await runtime.Maintenance.EnterAsync(operationId, "update.rollbackFailed", cancellationToken).ConfigureAwait(false);
        await runtime.Sources.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        return entered;
    }

    /// <summary>UDP停止要求が既存固定ジョブの実体と一致するかを確認します。</summary>
    /// <returns>正常終了を受け付けられるかです。</returns>
    public async Task<bool> CanStopAsync(Guid operationId, string fingerprint, CancellationToken cancellationToken = default)
    {
        LocalUpdateRecord? record = await new UpdateJournal(new UpdateLayout(Options, operationId), runtime.Cluster.LocalPeerId).ReadAsync(cancellationToken).ConfigureAwait(false);
        if (record is null || !string.Equals(record.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            return false;
        }
        bool ownInstance = record.Job.PreviousInstanceId == runtime.Cluster.LocalInstanceId || (record.Result.Phase == EUpdatePhase.Restoring && record.Result.InstanceId == runtime.Cluster.LocalInstanceId);
        return ownInstance && runtime.Maintenance.State?.OperationId == (record.Job.RollbackOf ?? operationId);
    }

    /// <summary>固定ジョブと元要求の停止受理だけを行います。</summary>
    /// <returns>操作記録保存前の受理判断です。</returns>
    public async Task<bool> AcceptStopAsync(LocalUpdateStopRequest request, CancellationToken cancellationToken = default)
    {
        if (!await CanStopAsync(request.OperationId, request.Fingerprint, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }
        ManagementCommand command = new(request.RequestId, runtime.Configuration.Cluster.Id, runtime.Cluster.LocalPeerId, runtime.Cluster.LocalInstanceId, request.ExpiresAt, "updates.local.stop", UpdateObserver.Payload(request));
        pendingStops.TryAdd(request.RequestId, command);
        return true;
    }

    /// <summary>元操作記録の完了と指紋を共通照会で確認してからHostを止めます。</summary>
    /// <returns>停止受付の確認完了です。</returns>
    public async ValueTask RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (Options.InterfaceId is null)
        {
            Infrastructure.Persistence.DurableEntry? identity = await runtime.Repository.ReadAsync("peer:bootstrap", "identity", cancellationToken).ConfigureAwait(false);
            string field = "InterfaceId";
            if (identity?.Value?.Json.TryGetProperty(field, out System.Text.Json.JsonElement selected) == true && selected.GetString() is string interfaceId)
            {
                Options = Options with { InterfaceId = interfaceId };
                Agent = new UpdatePeerAgent(Options, runtime.Cluster.LocalPeerId, runtime.Cluster.LocalInstanceId, arguments, launcher);
            }
        }
        foreach (KeyValuePair<Guid, ManagementCommand> pending in pendingStops)
        {
            ClusterRequestStatus status = await runtime.Cluster.QueryRequestAsync(ManagementWire.Request(pending.Value, false), cancellationToken).ConfigureAwait(false);
            if (status.Response?.Outcome == EOutcome.Succeeded)
            {
                pendingStops.TryRemove(pending.Key, out _);
                host.StopApplication();
            }
        }
    }
}
