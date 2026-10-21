using System.Diagnostics.CodeAnalysis;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting.Composition;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting.Workers;
using ReArtWord.Contents.BackgroundService.CoreService.Management;
using ReArtWord.Contents.BackgroundService.CoreService.Management.Configuration;
using ReArtWord.Contents.BackgroundService.CoreService.Management.Conflicts;
using ReArtWord.Contents.BackgroundService.CoreService.Management.Persistence;
using ReArtWord.Contents.BackgroundService.CoreService.Management.Snapshots;
using ReArtWord.Contents.BackgroundService.CoreService.Updates;
using ReArtWord.Contents.BackgroundService.CoreService.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Requests;
using ReArtWord.Contents.BackgroundService.Infrastructure.Collaboration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Loading;
using ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;
using ReArtWord.Contents.BackgroundService.Infrastructure.Secrets;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;

namespace ReArtWord.Contents.BackgroundService.CoreService;

/// <summary>明示した一つの設定ファイルからPC常駐Peerサービスを起動します。</summary>
internal static class Program
{
    /// <summary>通常実行・Windows Service・systemdで共通のHostを起動します。</summary>
    /// <returns>正常終了は0、起動不能は1です。</returns>
    // 抑制理由: エントリポイントで全所有資源をawait usingし、初期化例外を秘密を含まない起動結果へ変換します。
    [SuppressMessage("Reliability", "CA2007", Justification = "同期コンテキストのないエントリポイントでawait usingによりHost・ログ・通信の終了を確認します。"),
     SuppressMessage("Design", "CA1031", Justification = "最上位だけで起動失敗を終了コードへ変換し、設定や秘密を含み得る例外本文を出力しません。"),
     SuppressMessage("SourceQuality", "RAW510", Justification = "C#の実行エントリポイントMainはCancellationToken引数を追加できません。"),
     SuppressMessage("SourceQuality", "RAW601", Justification = "C#の実行エントリポイントMainはAsync接尾辞へ変更できません。"),
     SuppressMessage("SourceQuality", "RAW141", Justification = "標準GenericHostをIAsyncDisposableへ渡し、直後のawait usingで非同期サービスの解放まで確認します。"),
     SuppressMessage("Reliability", "CA2000", Justification = "ログinstanceをDIへ渡しますが所有権はMainにあり、await usingで実際の非同期解放を必ず待ちます。")]
    private static async Task<int> Main(string[] args)
    {
        try
        {
            string scriptArgument = "--script-worker";
            if (args is [string workerMode, string requestPath] && string.Equals(workerMode, scriptArgument, StringComparison.Ordinal))
            {
                await Nodes.Scripting.CSharpScriptWorker.RunAsync(requestPath).ConfigureAwait(false);
                return 0;
            }
            string strict = "strict", compatible = "compatible";
            if (OperatingSystem.IsWindows() && args is [string argument, string shell, string userSid, string readyPipe, string policy]
                && (string.Equals(policy, strict, StringComparison.Ordinal) || string.Equals(policy, compatible, StringComparison.Ordinal))
                && (string.Equals(argument, Terminal.TerminalIdentity.UserShellArgument, StringComparison.Ordinal) || string.Equals(argument, Terminal.TerminalIdentity.AdministratorShellArgument, StringComparison.Ordinal)))
            {
                return Terminal.WindowsUserShell.Run(shell, string.Equals(argument, Terminal.TerminalIdentity.AdministratorShellArgument, StringComparison.Ordinal), userSid, readyPipe, string.Equals(args[4], strict, StringComparison.Ordinal));
            }
            string desktopArgument = "--desktop-worker";
            string closeArgument = "--application-close-worker";
            if (OperatingSystem.IsWindows() && args is [string closeMode, string closePipe] && string.Equals(closeMode, closeArgument, StringComparison.Ordinal))
            {
                await Windows.WindowsApplicationWindow.RunAsync(closePipe).ConfigureAwait(false);
                return 0;
            }
            string displayArgument = "--device-display-worker";
            if (OperatingSystem.IsWindows() && args is [string displayMode, string displayPipe] && string.Equals(displayMode, displayArgument, StringComparison.Ordinal))
            {
                await DeviceManagement.ConsoleSettingsWorker.RunAsync(displayPipe).ConfigureAwait(false);
                return 0;
            }
            if (OperatingSystem.IsWindows() && args is [string mode, string pipeName, string encoder] && string.Equals(mode, desktopArgument, StringComparison.Ordinal))
            {
                await Desktop.DesktopWorker.RunAsync(pipeName, encoder).ConfigureAwait(false);
                return 0;
            }
            string historyArgument = "--history-maintenance";
            if (args is [string historyMode, string historyConfiguration, string historyOperation] && string.Equals(historyMode, historyArgument, StringComparison.Ordinal))
            {
                return await HistoryMaintenanceCommand.RunAsync(historyConfiguration, historyOperation).ConfigureAwait(false);
            }
            string configurationPath = ConfigurationPath(args);
            CommonConfiguration configuration = CommonConfigurationReader.Read(await File.ReadAllTextAsync(configurationPath).ConfigureAwait(false), Path.GetDirectoryName(configurationPath)!);
            using FileStream historyUsage = Infrastructure.Persistence.History.HistoryRuntimeLease.Acquire(configuration.DataDirectory);
            using RepositoryRuntime persistence = await RepositoryFactory.CreateRuntimeAsync(configuration.DataDirectory, configuration.Cluster.PeerId, configuration.Persistence).ConfigureAwait(false);
            IDurableRepository repository = persistence.Repository;
            SecretStore secrets = new(repository);
            await using WorkflowLogService log = new(configuration.Logs, repository, secrets) { RetentionHeld = true };
            await log.StartAsync().ConfigureAwait(false);
            int? logPort = null;
            if (log.Statistics.ListenEndpoint is string address && IPEndPoint.TryParse(address, out IPEndPoint? endpoint))
            {
                logPort = endpoint.Port;
            }
            await using PeerCluster cluster = new(configuration.Cluster with { LogPort = logPort }, repository);
            secrets.Replicator = cluster.ReplicateAsync;
            HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], ContentRootPath = AppContext.BaseDirectory });
            builder.Logging.ClearProviders();
            builder.Services.AddWindowsService(options => options.ServiceName = configuration.Updates.ServiceName ?? "ReArtWord.Contents.BackgroundService");
            builder.Services.AddSystemd();
            if (OperatingSystem.IsWindows())
            {
                builder.Services.AddHostedService(provider =>
                {
                    if (!OperatingSystem.IsWindows())
                    {
                        throw new PlatformNotSupportedException();
                    }
                    return new Infrastructure.DeviceManagement.Power.IdleShutdownWorker(
                        provider.GetRequiredService<ILogger<Infrastructure.DeviceManagement.Power.IdleShutdownWorker>>(), DeviceManagement.ConsoleInputAccess.ReadAsync);
                });
            }
            builder.Services.AddSingleton(configuration);
            builder.Services.AddSingleton(provider => new SessionFailureBoundary(provider.GetRequiredService<IWorkflowLog>(), configuration.Cluster.PeerId));
            if (configuration.Updates.InstallationRoot is not null && configuration.Updates.StagingRoot is not null && configuration.Updates.BackupRoot is not null)
            {
                builder.Services.AddHostedService(provider => new Infrastructure.Updates.Releases.UpdateRetentionWorker(token =>
                {
                    LocalUpdateAdapter? updater = provider.GetRequiredService<ServiceRuntime>().Updater;
                    if (updater is null)
                    {
                        return Task.CompletedTask;
                    }
                    return Infrastructure.Updates.Releases.UpdateRetention.TrimCoreAsync(updater.Options, configuration.Cluster.PeerId, Guid.NewGuid(), token);
                }));
            }
            builder.Services.AddSingleton<Files.Transfers.TransferConnections>();
            builder.Services.AddSingleton(provider => new ManagementTransport.Observers.PeerManagementClient(provider.GetRequiredService<Files.Transfers.TransferConnections>().Find));
            builder.Services.AddSingleton<Files.Transfers.FileCopyWorker>();
            builder.Services.AddSingleton<Files.Transfers.FileTransferStreams>();
            builder.Services.AddHostedService(provider => provider.GetRequiredService<Files.Transfers.FileTransferStreams>());
            builder.Services.AddSingleton<Files.Transfers.FileTransferQueue>();
            builder.Services.AddHostedService(provider => provider.GetRequiredService<Files.Transfers.FileTransferQueue>());
            builder.Services.AddSingleton<Desktop.DesktopService>();
            builder.Services.AddHostedService(provider => provider.GetRequiredService<Desktop.DesktopService>());
            builder.Services.AddSingleton<Terminal.TerminalService>();
            builder.Services.AddHostedService(provider => provider.GetRequiredService<Terminal.TerminalService>());
            builder.Services.AddSingleton<SignalScope.SignalScopeService>();
            builder.Services.AddHostedService(provider => provider.GetRequiredService<SignalScope.SignalScopeService>());
            builder.Services.AddSingleton<States.StateStreamService>();
            builder.Services.AddHostedService(provider => provider.GetRequiredService<States.StateStreamService>());
            builder.Services.AddSingleton(repository);
            builder.Services.AddSingleton(cluster);
            builder.Services.AddSingleton(secrets);
            builder.Services.AddSingleton<IWorkflowLog>(log);
            builder.Services.AddSingleton(log);
            builder.Services.AddSingleton(_ => new LogRetentionCoordinator(repository, log));
            builder.Services.AddSingleton(_ => new ServiceRuntime(configuration, repository, cluster, secrets, log));
            builder.Services.AddSingleton(provider => Management.DeviceManagement.Create(provider.GetRequiredService<ServiceRuntime>()));
            builder.Services.AddSingleton(_ => new Infrastructure.DeviceManagement.Power.Scheduling.HostPowerScheduleStore(cluster, repository));
            builder.Services.AddHostedService<DeviceManagement.HostPowerScheduleWorker>();
            builder.Services.AddSingleton(_ => new Infrastructure.Persistence.History.HistoryArchiveService(repository));
            builder.Services.AddHostedService<HistoryArchiveWorker>();
            builder.Services.AddHostedService<Files.FileReservationWorker>();
            builder.Services.AddSingleton(provider => new WorkflowRunService(provider.GetRequiredService<ServiceRuntime>()));
            builder.Services.AddSingleton(provider => new ServiceBootstrap(provider.GetRequiredService<ServiceRuntime>()));
            builder.Services.AddSingleton(provider => new CollaborationService(provider.GetRequiredService<ServiceRuntime>().Workflows, cluster));
            builder.Services.AddSingleton(provider => new DraftAutoApply(provider.GetRequiredService<ServiceRuntime>().Workflows,
                provider.GetRequiredService<CollaborationService>(), cluster, provider.GetRequiredService<ServiceRuntime>().Operational));
            builder.Services.AddHostedService(provider => new DraftAutoApplyWorker(provider.GetRequiredService<DraftAutoApply>()));
            builder.Services.AddSingleton<SetupManagement>();
            builder.Services.AddHostedService(provider => provider.GetRequiredService<SetupManagement>());
            builder.Services.AddHostedService(provider => new RuntimeWorker(provider.GetRequiredService<ServiceRuntime>(), provider.GetRequiredService<ServiceBootstrap>(), provider.GetRequiredService<IHostApplicationLifetime>(), provider.GetRequiredService<IHostLifetime>(), log, provider.GetRequiredService<LogRetentionCoordinator>()));
            IHost app = builder.Build();
            await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable hostLifetime = ((IAsyncDisposable)app).ConfigureAwait(false);
            ManagementRegistry management = CreateManagement(app.Services, log);
            _ = new PeerManagementEndpoint(cluster, management.ExecuteAsync, management.IsReadOnly, management.IsTransient);
            await app.StartAsync().ConfigureAwait(false);
            await app.WaitForShutdownAsync().ConfigureAwait(false);
            if (app.Services.GetRequiredService<ServiceRuntime>().StartupFailed)
            {
                return 1;
            }
            return 0;
        }
        catch (Exception)
        {
            await Console.Error.WriteLineAsync("Clutch-CS を起動できません。設定の型・指定IPとポート・保存先の権限・導入ファイルを確認してください。").ConfigureAwait(false);
            return 1;
        }
    }

    /// <summary>管理操作をHTTP待受に依存しない実サービスへ接続します。</summary>
    /// <returns>読取りと変更を区別する登録済み操作です。</returns>
    private static ManagementRegistry CreateManagement(IServiceProvider services, WorkflowLogService log)
    {
        ServiceRuntime runtime = services.GetRequiredService<ServiceRuntime>();
        ManagementRegistry management = new();
        management.AdmissionCheck = operation => DetachedManagementPolicy.GetProblem(operation, runtime.Cluster.IsDetached, runtime.Configuration.Detached);
        CoreManagement.Register(management, runtime, log);
        services.GetRequiredService<SetupManagement>().Register(management);
        Management.DeviceManagement.Register(management, services.GetRequiredService<Infrastructure.DeviceManagement.DeviceManagementService>());
        Infrastructure.DeviceManagement.Power.Scheduling.HostPowerScheduleStore powerSchedules = services.GetRequiredService<Infrastructure.DeviceManagement.Power.Scheduling.HostPowerScheduleStore>();
        management.Read("host.power-schedules.read", async (body, cancellation) => await powerSchedules.ReadAsync(ManagementRegistry.Body<Contracts.DeviceManagement.PowerSchedules.HostPowerScheduleReadRequest>(body), cancellation).ConfigureAwait(false));
        management.Write("host.power-schedules.save", async (body, cancellation) => await powerSchedules.SaveAsync(ManagementRegistry.Body<Contracts.DeviceManagement.PowerSchedules.HostPowerScheduleSaveRequest>(body), cancellation).ConfigureAwait(false));
        management.Read("history.archive.status", async (_, cancellation) => await services.GetRequiredService<Infrastructure.Persistence.History.HistoryArchiveService>().ReadStatusAsync(cancellation).ConfigureAwait(false));
        management.Read("history.checkpoints", async (_, cancellation) => await services.GetRequiredService<Infrastructure.Persistence.History.HistoryArchiveService>().ReadCheckpointsAsync(cancellation).ConfigureAwait(false));
        FileTransferManagement.Register(management, runtime.FileTransactions.Files);
        services.GetRequiredService<Files.Transfers.FileTransferStreams>().Register(management);
        services.GetRequiredService<Files.Transfers.FileTransferQueue>().Register(management);
        services.GetRequiredService<Desktop.DesktopService>().Register(management);
        services.GetRequiredService<Terminal.TerminalService>().Register(management);
        services.GetRequiredService<SignalScope.SignalScopeService>().Register(management);
        services.GetRequiredService<States.StateStreamService>().Register(management);
        ConflictManagement.Register(management, runtime);
        DetachedWorkflowManagement.Register(management, runtime);
        AvailabilityManagement.Register(management, runtime);
        InternalDataManagement.Register(management, runtime);
        WorkflowManagement.Register(management, runtime, services.GetRequiredService<WorkflowRunService>());
        ProcessManagement.Register(management, runtime);
        SafetyManagement.Register(management, runtime, log);
        SnapshotManagement.Register(management, runtime);
        ObservabilityManagement.Register(management, runtime, log, services.GetRequiredService<LogRetentionCoordinator>());
        CollaborationService collaboration = services.GetRequiredService<CollaborationService>();
        runtime.Snapshots.Editor = collaboration;
        CollaborationManagement.Register(management, collaboration, services.GetRequiredService<DraftAutoApply>());
        runtime.Updater = UpdateManagement.Register(management, runtime, runtime.Configuration.Updates, services.GetRequiredService<IHostApplicationLifetime>());
        return management;
    }

    /// <summary>不明な引数を黙って無視せず設定ファイルを選びます。</summary>
    /// <returns>設定ファイルの絶対パスです。</returns>
    private static string ConfigurationPath(string[] args)
    {
        string option = "--config";
        if (args.Length == 0)
        {
            return Path.Combine(AppContext.BaseDirectory, "contents.toml");
        }
        if (args.Length != 2 || !string.Equals(args[0], option, StringComparison.Ordinal))
        {
            throw new ArgumentException("--config <絶対または相対パス>を指定してください。", nameof(args));
        }
        return Path.GetFullPath(args[1]);
    }
}
