using System.Diagnostics;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Local;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Health;
using ReArtWord.Contents.BackgroundService.Runtime.Processes.Commands;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Local;

/// <summary>更新対象サービスの明示停止・起動を実OS操作へ接続します。</summary>
internal static class UpdateProcessControl
{
    /// <summary>設定したサービス管理経路で停止し、実PIDの終了を確認します。</summary>
    /// <returns>停止確認の結果です。</returns>
    internal static async Task<EOutcome> StopAsync(LocalUpdateJob job, ProcessIdentity identity, CancellationToken cancellationToken = default)
    {
        using Process? process = ProcessOs.Open(identity);
        if (process is null)
        {
            return EOutcome.Succeeded;
        }
        return await StopAsync(job, identity, process, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>起動時または停止前に確保したハンドルを、通常停止と強制停止の両方で維持します。</summary>
    /// <returns>対象実体の終了確認です。ハンドルは呼出元が破棄します。</returns>
    internal static async Task<EOutcome> StopAsync(LocalUpdateJob job, ProcessIdentity identity, Process process, CancellationToken cancellationToken = default)
    {
        if (ProcessOs.Identity(process) != identity)
        {
            return EOutcome.Unknown;
        }
        if (job.Options.ServiceKind == EUpdateServiceKind.DirectProcess)
        {
            await RequestHostStopAsync(job, identity, cancellationToken).ConfigureAwait(false);
        }
        if (job.Options.ServiceKind != EUpdateServiceKind.DirectProcess)
        {
            try
            {
                await ServiceCommandAsync(job.Options, false, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or ProcessStopUnconfirmedException)
            {
                if (!process.HasExited)
                {
                    return EOutcome.Unknown;
                }
            }
        }
        EProcessStopPolicy policy = EProcessStopPolicy.WaitForManual;
        if (job.Options.ForceStopOnTimeout)
        {
            policy = EProcessStopPolicy.Force;
        }
        return await ProcessOs.StopAsync(process, identity, policy, job.Options.StopTimeoutMilliseconds, new DefaultProcessPlatform(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>明示ジョブに対する常駐サービスの正常終了入口を呼びます。</summary>
    /// <returns>UDP確認またはOS側停止へ移る判断です。</returns>
    private static async Task RequestHostStopAsync(LocalUpdateJob job, ProcessIdentity identity, CancellationToken cancellationToken = default)
    {
        UpdateObserver observer = new(UpdateObserver.Binding(job.Options), job.Options.ClusterId!);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = observer.ConfigureAwait(false);
        using CancellationTokenSource timeout = new(TimeSpan.FromMilliseconds(Math.Max(2000, job.Options.StopTimeoutMilliseconds)));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        try
        {
            Contracts.Cluster.PeerInfo peer = await observer.FindAsync(job.PeerId, cancellationToken: linked.Token).ConfigureAwait(false);
            Contracts.Management.ManagementReply health = await observer.SendAsync(job.PeerId, peer.InstanceId, "health", null, true, linked.Token).ConfigureAwait(false);
            string pidField = "processId";
            string startedField = "processStartedAt";
            if (health.Value is null || health.Value.Json.GetProperty(pidField).GetInt32() != identity.ProcessId || health.Value.Json.GetProperty(startedField).GetDateTimeOffset() != identity.StartedAt)
            {
                return;
            }
            LocalUpdateStopRequest stop = new(job.OperationId, ContractJson.Hash(job), Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1));
            Contracts.Management.ManagementCommand command = new(stop.RequestId, job.Options.ClusterId!, job.PeerId, peer.InstanceId, stop.ExpiresAt, "updates.local.stop", UpdateObserver.Payload(stop));
            await observer.SendAsync(command, false, linked.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <summary>固定したentryPointと引数から実サービスを再起動します。</summary>
    /// <returns>呼出元が終了確認まで保持するハンドルです。OSサービスなら稼働確認待ちのnullです。</returns>
    internal static async Task<Process?> StartAsync(LocalUpdateJob job, CancellationToken cancellationToken = default)
    {
        if (job.Options.ServiceKind != EUpdateServiceKind.DirectProcess)
        {
            await ServiceCommandAsync(job.Options, true, cancellationToken).ConfigureAwait(false);
            return null;
        }
        cancellationToken.ThrowIfCancellationRequested();
        string entry = Path.Combine(job.Options.InstallationRoot!, job.Manifest.EntryPoint.Replace('/', Path.DirectorySeparatorChar));
        string executable = entry;
        List<string> arguments = [];
        if (job.LauncherExecutablePath is not null)
        {
            executable = job.LauncherExecutablePath;
            arguments.AddRange(job.LauncherArguments ?? []);
            arguments.Add(entry);
        }
        arguments.AddRange(job.ApplicationArguments);
        ProcessDefinition definition = new("contents-service", EProcessMode.Managed, executable, arguments, job.Options.InstallationRoot!);
        return ProcessOs.StartTracked(definition);
    }

    /// <summary>shellを使わずOS サービス managerへ固定verbを渡します。</summary>
    /// <returns>サービス manager要求の完了です。</returns>
    private static async Task ServiceCommandAsync(UpdatesOptions options, bool start, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ServiceName);
        bool validName = options.ServiceName.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or '@');
        if (!validName)
        {
            throw new InvalidDataException("service識別子が不正です。");
        }
        string executable;
        string[] arguments;
        string verb = "stop";
        if (start)
        {
            verb = "start";
        }
        switch (options.ServiceKind)
        {
            case EUpdateServiceKind.WindowsService when OperatingSystem.IsWindows():
                executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe");
                arguments = [verb, options.ServiceName];
                break;
            case EUpdateServiceKind.Systemd when OperatingSystem.IsLinux():
                executable = "/usr/bin/systemctl";
                arguments = [verb, options.ServiceName];
                break;
            case EUpdateServiceKind.Launchd when OperatingSystem.IsMacOS():
                executable = "/bin/launchctl";
                arguments = ["bootout", "system/" + options.ServiceName];
                if (start)
                {
                    arguments = ["bootstrap", "system", "/Library/LaunchDaemons/" + options.ServiceName + ".plist"];
                }
                break;
            default:
                throw new InvalidDataException("明示service方式がこのOSと一致しません。");
        }
        using CancellationTokenSource timeout = new(TimeSpan.FromMilliseconds(options.StopTimeoutMilliseconds));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        _ = await BoundedProcessRunner.RunAsync(executable, arguments, linked.Token).ConfigureAwait(false);
    }
}
