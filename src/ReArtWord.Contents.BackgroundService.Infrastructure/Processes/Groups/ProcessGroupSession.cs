using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Processes.Groups;

/// <summary>一つのグループの設定変更、操作受付、進行を直列化します。</summary>
internal sealed class ProcessGroupSession : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ProcessGroupStore store;
    private readonly ProcessService processes;
    private readonly ProcessGroupOwnership ownership;
    private readonly TimeProvider clock;
    private readonly IWorkflowLog? log;
    private readonly string peerId;
    private readonly ProcessGroupExecution execution;
    private string? stateVersion;
    private ProcessGroupRun run;
    private RegisteredProcessGroup registration;
    private bool deleted;
    private bool reload;
    private bool monitoringLogged;

    /// <summary>保存済みの設定と操作を引き継ぎます。</summary>
    internal ProcessGroupSession(ProcessGroupStore store, ProcessService processes, ProcessGroupOwnership ownership, TimeProvider clock, string peerId, IWorkflowLog? log,
        string version, ProcessGroupDefinition definition, string? stateVersion, ProcessGroupRun run)
    {
        this.store = store;
        this.processes = processes;
        this.ownership = ownership;
        this.clock = clock;
        this.peerId = peerId;
        this.log = log;
        this.stateVersion = stateVersion;
        this.run = run;
        registration = new(version, definition, run.Status(definition.Id));
        execution = new(processes, clock, definition.Id);
        ownership.Set(definition, run.Active);
    }

    /// <summary>APIが副作用なしに読み取れる、不変の最新表示です。</summary>
    internal RegisteredProcessGroup Registration => Volatile.Read(ref registration);

    /// <summary>進行中の設定を変更せず、削除時もアプリケーション自体は停止しません。</summary>
    /// <returns>期待した登録版へ適用した結果です。</returns>
    internal async ValueTask<EOutcome> UpdateAsync(ProcessGroupDefinition? definition, string expectedVersion, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReloadAsync(cancellationToken).ConfigureAwait(false);
            if (deleted || !string.Equals(registration.Version, expectedVersion, StringComparison.Ordinal)
                || (run.Active && (definition is not null || run.Phase != EProcessGroupPhase.Paused)))
            {
                return EOutcome.NotExecuted;
            }
            EOutcome outcome = await store.RegisterAsync(definition, registration.Definition.Id, expectedVersion, cancellationToken).ConfigureAwait(false);
            if (outcome == EOutcome.Succeeded)
            {
                if (definition is null)
                {
                    deleted = true;
                    ownership.Remove(registration.Definition.Id);
                }
                else
                {
                    (string version, ProcessGroupDefinition saved) = (await store.ListAsync(cancellationToken).ConfigureAwait(false)).Single(item => string.Equals(item.Definition.Id, definition.Id, StringComparison.Ordinal));
                    registration = new(version, saved, run.Status(saved.Id));
                    ownership.Set(saved, run.Active);
                }
            }
            return outcome;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>実プロセスを操作する前に、対象の登録版と起動順を固定します。</summary>
    /// <returns>受付結果です。完了はStatusで別に確認します。</returns>
    internal async ValueTask<ProcessGroupOperationResult> CommandAsync(EProcessGroupCommand command, string? expectedVersion = null, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReloadAsync(cancellationToken).ConfigureAwait(false);
            if (expectedVersion is not null && !string.Equals(expectedVersion, registration.Version, StringComparison.Ordinal))
            {
                return new(EOutcome.NotExecuted, registration.Status, "process.registrationChanged");
            }
            if (deleted || !Enum.IsDefined(command) || (run.Active && run.Phase != EProcessGroupPhase.Paused && command != EProcessGroupCommand.Stop))
            {
                return new(EOutcome.NotExecuted, registration.Status, "processGroup.operationInProgress");
            }
            IReadOnlyList<RegisteredProcess> applications = await processes.ListAsync(cancellationToken).ConfigureAwait(false);
            return await BeginAsync(command, applications, false, null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>グループごとに独立して監視し、一つの停止待ちを別のグループへ波及させません。</summary>
    /// <returns>一段階の確認結果です。</returns>
    internal async ValueTask TickAsync(IReadOnlyList<RegisteredProcess> applications, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReloadAsync(cancellationToken).ConfigureAwait(false);
            if (deleted)
            {
                return;
            }
            if (run.Active || (run.Suppressed && registration.Definition.AutomaticMonitoring))
            {
                foreach (ProcessGroupMember member in registration.Definition.Members)
                {
                    await processes.ObserveAsync(member.ApplicationId, cancellationToken).ConfigureAwait(false);
                }
            }
            if (run.Phase == EProcessGroupPhase.Paused)
            {
                return;
            }
            if (run.Active)
            {
                ProcessGroupRun? next = await execution.StepAsync(run, SaveAsync, cancellationToken).ConfigureAwait(false);
                if (next is not null && next != run)
                {
                    await SaveAsync(next, cancellationToken).ConfigureAwait(false);
                }
                return;
            }
            ProcessGroupDefinition definition = registration.Definition;
            if (!definition.AutomaticMonitoring || run.Suppressed)
            {
                return;
            }
            if (!monitoringLogged)
            {
                await WorkflowLogBoundary.WriteAsync(log, new(Guid.NewGuid(), clock.GetUtcNow(), "INFO", peerId, "ApplicationGroup.MonitoringStarted",
                    Data: NodeValue.FromObject(new { groupId = definition.Id, applications = definition.Members.Select(member => member.ApplicationId).ToArray() })), cancellationToken).ConfigureAwait(false);
                monitoringLogged = true;
            }
            List<string> failed = [];
            bool full = definition.RecoveryMode == EProcessGroupRecoveryMode.EntireGroup;
            foreach (ProcessGroupMember member in definition.Members)
            {
                RegisteredProcess? application = applications.FirstOrDefault(item => string.Equals(item.Definition.Id, member.ApplicationId, StringComparison.Ordinal));
                if (application is null)
                {
                    return;
                }
                ProcessOperationResult observed = await processes.ObserveAsync(member.ApplicationId, cancellationToken).ConfigureAwait(false);
                if (observed.Status is not { } status || observed.Outcome != EOutcome.Succeeded)
                {
                    if (full)
                    {
                        return;
                    }
                    continue;
                }
                if (full && (status.RecoverySuppressed || status.PlannedStop))
                {
                    return;
                }
                if (ProcessGroupRules.CanRecover(application.Definition, status, processes.Options)
                    && !await ProcessGroupRules.AwaitingBootAsync(processes, application.Definition, cancellationToken).ConfigureAwait(false))
                {
                    failed.Add(member.ApplicationId);
                }
            }
            if (failed.Count != 0)
            {
                if (full)
                {
                    foreach (ProcessGroupMember member in definition.Members)
                    {
                        ProcessDefinition application = applications.Single(item => string.Equals(item.Definition.Id, member.ApplicationId, StringComparison.Ordinal)).Definition;
                        if (await ProcessGroupRules.AwaitingBootAsync(processes, application, cancellationToken).ConfigureAwait(false))
                        {
                            return;
                        }
                    }
                }
                await BeginAsync(full ? EProcessGroupCommand.Restart
                                      : EProcessGroupCommand.Start, applications, true, failed, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>所有する監視が停止してから排他資源を破棄します。</summary>
    public void Dispose() => gate.Dispose();

    /// <summary>副作用前にグループを占有し、現在版を保存します。</summary>
    /// <returns>受付済みの操作識別子です。</returns>
    private async ValueTask<ProcessGroupOperationResult> BeginAsync(EProcessGroupCommand command, IReadOnlyList<RegisteredProcess> applications,
        bool automatic, IReadOnlyCollection<string>? failed, CancellationToken cancellationToken = default)
    {
        ProcessGroupDefinition definition = registration.Definition;
        string? invalid = ProcessGroupRules.Validate(definition, applications);
        if (invalid is not null)
        {
            return new(EOutcome.NotExecuted, registration.Status, invalid);
        }
        bool recoverOnly = automatic && definition.RecoveryMode == EProcessGroupRecoveryMode.FailedApplications;
        ProcessGroupTarget[] targets = definition.Members.Where(member => !recoverOnly || failed!.Contains(member.ApplicationId))
            .Select(member => new ProcessGroupTarget(member.ApplicationId, applications.Single(item => string.Equals(item.Definition.Id, member.ApplicationId, StringComparison.Ordinal)).Version, member.DelayMilliseconds)).ToArray();
        if (command != EProcessGroupCommand.Stop && targets.Any(target => !processes.IsGroupLaunchAvailable(applications.Single(application => string.Equals(application.Definition.Id, target.ApplicationId, StringComparison.Ordinal)).Definition)))
        {
            return new(EOutcome.NotExecuted, registration.Status, "process.interactiveUserUnavailable");
        }
        ownership.SetActive(definition.Id, true);
        bool stopping = command is EProcessGroupCommand.Restart or EProcessGroupCommand.Stop;
        DateTimeOffset now = clock.GetUtcNow();
        EProcessGroupPhase phase = EProcessGroupPhase.Waiting;
        int index = 0;
        DateTimeOffset? nextAt = now.AddMilliseconds(targets[0].DelayMilliseconds);
        if (stopping)
        {
            phase = EProcessGroupPhase.Stopping;
            index = targets.Length - 1;
            nextAt = null;
        }
        DateTimeOffset? startNotBefore = null;
        if (automatic && !recoverOnly)
        {
            startNotBefore = now.AddMilliseconds(definition.RecoveryDelayMilliseconds);
        }
        ProcessGroupRun next = new(Guid.NewGuid(), ContractJson.Copy(definition), targets, command, automatic, recoverOnly,
            phase, index, NextAt: nextAt, StartNotBefore: startNotBefore,
            Suppressed: command == EProcessGroupCommand.Stop);
        if (!await SaveAsync(next, cancellationToken).ConfigureAwait(false))
        {
            return new(EOutcome.Unknown, registration.Status, "processGroup.stateUnconfirmed");
        }
        return new(EOutcome.Succeeded, registration.Status);
    }

    /// <summary>確定保存できた位置だけを公開し、不明な保存結果では再送せず再読取りへ戻します。</summary>
    /// <returns>この変更を保存できたかです。</returns>
    private async ValueTask<bool> SaveAsync(ProcessGroupRun next, CancellationToken cancellationToken = default)
    {
        ProcessGroupRun previous = run;
        string id = registration.Definition.Id;
        reload = true;
        EOutcome outcome = await store.SaveRunAsync(id, stateVersion, next, cancellationToken).ConfigureAwait(false);
        await ReloadAsync(cancellationToken).ConfigureAwait(false);
        if (outcome != EOutcome.Succeeded)
        {
            return false;
        }
        string? message = null;
        if (previous.OperationId != run.OperationId)
        {
            message = "ApplicationGroup.OperationStarted";
        }
        else if (previous.Phase != run.Phase && run.Phase is EProcessGroupPhase.Idle or EProcessGroupPhase.Paused)
        {
            message = run.Phase == EProcessGroupPhase.Idle ? "ApplicationGroup.OperationCompleted"
                                                           : "ApplicationGroup.OperationPaused";
        }
        if (message is not null)
        {
            string level = run.Phase == EProcessGroupPhase.Paused ? "WARN"
                                                                  : "INFO";
            await WorkflowLogBoundary.WriteAsync(log, new(Guid.NewGuid(), clock.GetUtcNow(), level, peerId, message,
                Data: NodeValue.FromObject(new { groupId = id, operationId = run.OperationId, previousOperationId = previous.OperationId, run.Command, run.Automatic, run.Outcome, run.Reason })), cancellationToken).ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>保存応答を失った場合も、次の副作用より前に確定位置を読み直します。</summary>
    /// <returns>不明な保存結果の照合完了です。</returns>
    private async ValueTask ReloadAsync(CancellationToken cancellationToken = default)
    {
        if (reload)
        {
            string id = registration.Definition.Id;
            (stateVersion, run) = await store.ReadRunAsync(id, cancellationToken).ConfigureAwait(false);
            ownership.SetActive(id, run.Active);
            registration = registration with { Status = run.Status(id) };
            reload = false;
        }
    }
}
