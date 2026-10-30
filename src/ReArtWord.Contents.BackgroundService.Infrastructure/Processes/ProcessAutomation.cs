using System.Diagnostics.CodeAnalysis;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Processes;

/// <summary>ブラウザやWFの寿命に依存せず、登録したアプリケーションの監視と予定を処理します。</summary>
public sealed class ProcessAutomation
{
    private readonly ProcessService service;
    private readonly string peerId;
    private readonly IWorkflowLog? log;
    private readonly TimeProvider clock;
    private readonly List<Application> applications = [];
    private long revision = -1;
    private DateTimeOffset? activatedAt;
    private string? failure;

    /// <summary>実プロセス操作の所有者と記録先を受け取ります。</summary>
    public ProcessAutomation(ProcessService service, string peerId, IWorkflowLog? log = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(peerId);
        this.service = service;
        this.peerId = peerId;
        this.log = log;
        clock = timeProvider ?? TimeProvider.System;
    }

    /// <summary>運用可能な間だけ自動操作し、過ぎた暦の予定と未受付のPC起動を区別します。</summary>
    /// <returns>所有者の停止までの作業です。</returns>
    public async Task RunAsync(Func<bool> canOperate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(canOperate);
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(service.Options.PollMilliseconds), clock);
        do
        {
            await TickAsync(canOperate(), cancellationToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>登録変更時だけ一覧を読み、予定が到来した場合だけその受付状態を読み書きします。</summary>
    /// <returns>同じ監視所有者から直列に呼ぶ一巡の完了です。</returns>
    // 抑制理由: 監視の所有境界で非致命的な失敗を記録し、他の常駐処理を停止させません。
    [SuppressMessage("Design", "CA1031", Justification = "登録読取の一時障害を次の巡回で再確認し、例外の種類の変化だけを記録します。")]
    public async Task TickAsync(bool canOperate, CancellationToken cancellationToken = default)
    {
        if (!canOperate)
        {
            activatedAt = null;
            return;
        }
        activatedAt ??= clock.GetUtcNow();
        try
        {
            await service.Groups.InitializeAsync(cancellationToken).ConfigureAwait(false);
            long current = service.RegistrationRevision;
            if (current != revision)
            {
                IReadOnlyList<RegisteredProcess> registrations = await service.ListAsync(cancellationToken).ConfigureAwait(false);
                Application[] next = registrations.Where(entry => entry.Definition.AutomaticMonitoring || entry.Definition.Schedules?.Any(schedule => schedule.Enabled) == true)
                    .Select(entry => applications.FirstOrDefault(old => string.Equals(old.Registration.Version, entry.Version, StringComparison.Ordinal) && string.Equals(old.Registration.Definition.Id, entry.Definition.Id, StringComparison.Ordinal)) ?? new Application(entry)).ToArray();
                applications.Clear();
                applications.AddRange(next);
                revision = current;
            }
            await Parallel.ForEachAsync(applications, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
                (application, token) => CheckAsync(application, token)).ConfigureAwait(false);
            failure = null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException && !cancellationToken.IsCancellationRequested)
        {
            string type = exception.GetType().FullName ?? exception.GetType().Name;
            if (!string.Equals(failure, type, StringComparison.Ordinal))
            {
                failure = type;
                await WriteFailureAsync(null, type, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>一登録の遅い停止や障害が、他登録の監視を無制限に塞がないようにします。</summary>
    /// <returns>現在の一登録を確認した結果です。</returns>
    // 抑制理由: 一アプリケーションの非致命的な失敗はその登録内で隔離します。
    [SuppressMessage("Design", "CA1031", Justification = "OSや予定の確認失敗を、Core全体や他のアプリケーションへ波及させません。")]
    private async ValueTask CheckAsync(Application application, CancellationToken cancellationToken = default)
    {
        try
        {
            ProcessDefinition definition = application.Registration.Definition;
            bool initialRead = !application.SchedulesLoaded;
            if (definition.Mode == EProcessMode.Managed && initialRead)
            {
                await LoadSchedulesAsync(application, cancellationToken).ConfigureAwait(false);
            }
            if (definition.AutomaticMonitoring && !service.Groups.Managed(definition.Id))
            {
                bool deferRecovery = definition.Mode == EProcessMode.Managed && AwaitingBootSchedule(application);
                ProcessOperationResult result = await service.MonitorAsync(application.Registration, deferRecovery, cancellationToken).ConfigureAwait(false);
                if (!application.MonitoringStarted && result.Status is not null)
                {
                    application.MonitoringStarted = true;
                    await WorkflowLogBoundary.WriteAsync(log, new(Guid.NewGuid(), clock.GetUtcNow(), "INFO", peerId, "Application.MonitoringStarted",
                        Data: NodeValue.FromObject(new { applicationId = definition.Id, applicationName = definition.DisplayName,
                            instanceId = result.Status.InstanceId, pid = result.Status.Identity?.ProcessId, recoveryAttempts = result.Status.RecoveryAttempts })), cancellationToken).ConfigureAwait(false);
                }
                string missingExecutable = "process.executableUnavailable", missingUser = "process.interactiveUserUnavailable";
                if (result.Reason is { } unavailable && (string.Equals(unavailable, missingExecutable, StringComparison.Ordinal) || string.Equals(unavailable, missingUser, StringComparison.Ordinal)))
                {
                    await ReportAsync(application, unavailable, cancellationToken).ConfigureAwait(false);
                    return;
                }
                if (result.Outcome == EOutcome.Unknown)
                {
                    await ReportAsync(application, result.Reason ?? "process.operationUnconfirmed", cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
            if (definition.Mode == EProcessMode.Managed)
            {
                foreach (ProcessSchedule schedule in definition.Schedules ?? [])
                {
                    if (service.IsScheduleDue(definition, schedule, application.Schedules[schedule.Id], initialRead))
                    {
                        ProcessScheduleStatus next = await service.RunScheduleAsync(application.Registration, schedule, activatedAt!.Value, cancellationToken).ConfigureAwait(false);
                        string groupBusy = "process.groupOperationInProgress";
                        if (string.Equals(next.Reason, groupBusy, StringComparison.Ordinal))
                        {
                            continue;
                        }
                        application.Schedules[schedule.Id] = next;
                        if (application.Schedules[schedule.Id].Outcome == EOutcome.Unknown)
                        {
                            await ReportAsync(application, application.Schedules[schedule.Id].Reason ?? "process.scheduleUnconfirmed", cancellationToken).ConfigureAwait(false);
                            return;
                        }
                    }
                }
            }
            application.Failure = null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException && !cancellationToken.IsCancellationRequested)
        {
            await ReportAsync(application, exception.GetType().FullName ?? exception.GetType().Name, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>初回と登録変更後だけ、起動予定の受付状態をまとめて読みます。</summary>
    /// <returns>予定状態をすべて確認した時点の完了です。</returns>
    private async ValueTask LoadSchedulesAsync(Application application, CancellationToken cancellationToken = default)
    {
        Dictionary<string, ProcessScheduleStatus> schedules = new(StringComparer.Ordinal);
        await foreach (ProcessScheduleStatus status in service.ReadSchedulesAsync(application.Registration.Definition, cancellationToken).ConfigureAwait(false))
        {
            schedules.Add(status.Id, status);
        }
        application.Schedules.Clear();
        foreach ((string id, ProcessScheduleStatus status) in schedules)
        {
            application.Schedules.Add(id, status);
        }
        application.SchedulesLoaded = true;
    }

    /// <summary>最初のPC起動予定を待つ間は、前回の故障からの自動復旧で起動しません。</summary>
    /// <returns>監視を続け、自動復旧だけを保留するかです。</returns>
    private static bool AwaitingBootSchedule(Application application)
    {
        bool pending = false;
        foreach (ProcessSchedule schedule in application.Registration.Definition.Schedules ?? [])
        {
            if (!schedule.Enabled || !ProcessSchedulePlan.IsBoot(schedule))
            {
                continue;
            }
            ProcessScheduleStatus status = application.Schedules[schedule.Id];
            if (status.NextAt is null && status.Outcome.HasValue && status.Outcome != EOutcome.Unknown)
            {
                return false;
            }
            pending |= status.NextAt.HasValue || status.Outcome == EOutcome.Unknown;
        }
        return pending;
    }

    /// <summary>同じ失敗が継続する間は重複記録しません。</summary>
    /// <returns>必要な一件の通知です。</returns>
    private async ValueTask ReportAsync(Application application, string reason, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(application.Failure, reason, StringComparison.Ordinal))
        {
            application.Failure = reason;
            await WriteFailureAsync(application.Registration.Definition.Id, reason, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>実行引数や例外本文を含めず、所有Peerと登録IDで障害を辿れるようにします。</summary>
    /// <returns>非阻害な記録です。</returns>
    private ValueTask WriteFailureAsync(string? application, string reason, CancellationToken cancellationToken = default)
        => WorkflowLogBoundary.WriteAsync(log, new(Guid.NewGuid(), clock.GetUtcNow(), "WARN", peerId, "Application.Automation",
            Data: NodeValue.FromObject(new { application, reason })), cancellationToken);

    /// <summary>一登録の現在版と有限個の予定だけを保持します。</summary>
    private sealed record Application(RegisteredProcess registration)
    {
        internal RegisteredProcess Registration
        {
            get;
        } = registration;

        internal Dictionary<string, ProcessScheduleStatus> Schedules
        {
            get;
        } = new(StringComparer.Ordinal);

        internal bool SchedulesLoaded
        {
            get;
            set;
        }

        internal bool MonitoringStarted
        {
            get;
            set;
        }

        internal string? Failure
        {
            get;
            set;
        }
    }
}
