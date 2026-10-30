using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Runtime.CompilerServices;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes.Groups;
using ReArtWord.Contents.BackgroundService.Runtime.Processes;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Processes;

/// <summary>明示登録とアプリケーションごとの排他で実プロセス操作を提供します。</summary>
public sealed class ProcessService : IProcessService, IDisposable
{
    private readonly ProcessRegistry registry;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new(StringComparer.Ordinal);
    private readonly ProcessHandleMonitor monitor;
    private readonly ProcessReplacementProbe replacements = new();
    private readonly TimeProvider clock;
    private readonly ProcessLifecyclePublisher events;
    private readonly Func<SystemBootSession?> boot;
    private long registrationRevision;
    private readonly ProcessGroupOwnership ownership = new();

    /// <summary>この所有者で確定した登録変更だけを監視ループへ通知します。</summary>
    internal long RegistrationRevision => Volatile.Read(ref registrationRevision);

    /// <summary>Peerローカルの共通リポジトリと復旧既定を使用します。</summary>
    public ProcessService(IDurableRepository repository, string peerId, ProcessOptions options, TimeProvider? timeProvider = null, IWorkflowEvents? events = null, IProcessPlatform? platform = null,
        Func<SystemBootSession?>? bootSession = null, IWorkflowLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(peerId);
        ArgumentNullException.ThrowIfNull(options);
        if (options.DefaultRecoveryAttempts is < 0 || options.DefaultReadyTimeoutMilliseconds is <= 0 || options.RecoveryDelayMilliseconds < 1 || options.PollMilliseconds < 1)
        {
            throw new ArgumentException("process共通既定は有限の正数または明示無制限にしてください。", nameof(options));
        }
        boot = bootSession ?? SystemBootObservation.Read;
        registry = new ProcessRegistry(repository, peerId, boot);
        clock = timeProvider ?? TimeProvider.System;
        this.events = new(peerId, events, clock, log);
        monitor = new(platform ?? new DefaultProcessPlatform());
        Options = options;
        Groups = new(repository, peerId, this, ownership, clock, log);
    }

    /// <summary>現在の明示共通既定です。</summary>
    public ProcessOptions Options
    {
        get;
    }

    /// <summary>同じPCの登録アプリケーションを連携して復旧するグループです。</summary>
    public ProcessGroupService Groups
    {
        get;
    }

    /// <summary>GUIとCLIで登録版を確認する一覧です。</summary>
    /// <returns>Peerローカルの登録一覧です。</returns>
    public ValueTask<IReadOnlyList<RegisteredProcess>> ListAsync(CancellationToken cancellationToken = default)
        => registry.ListAsync(cancellationToken);

    /// <summary>保存時は実行物の実在を要求せず登録内容の意味だけを確認します。</summary>
    /// <returns>期待版に対する登録結果です。</returns>
    // 抑制理由: アプリケーションの共有gateはサービスが所有し、個別処理はWaitとReleaseだけを行います。
    [SuppressMessage("SourceQuality", "RAW141", Justification = "service所有の共有gateをここで破棄すると後続操作を壊します。")]
    public async ValueTask<EOutcome> RegisterAsync(ProcessDefinition definition, string? expectedVersion = null, CancellationToken cancellationToken = default)
    {
        if (!ProcessDefinitionValidation.IsValid(definition, clock, boot))
        {
            return EOutcome.NotExecuted;
        }
        await Groups.InitializeAsync(cancellationToken).ConfigureAwait(false);
        ProcessDefinition owned = ContractJson.Copy(definition);
        SemaphoreSlim gate = Gate(definition.Id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (ownership.Check(definition.Id, EProcessGroupAccess.Edit, null) is not null)
            {
                return EOutcome.NotExecuted;
            }
            EOutcome result = await registry.RegisterAsync(owned, expectedVersion, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            if (result == EOutcome.Succeeded)
            {
                Interlocked.Increment(ref registrationRevision);
            }
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>起動・復旧と直列化して登録を削除し、実行中のアプリケーションは維持します。</summary>
    /// <returns>確認した登録版に対する削除結果です。</returns>
    // 抑制理由: 同じ登録を操作中の呼出しが使うため、共有gateはサービス終了時に解放します。
    [SuppressMessage("SourceQuality", "RAW141", Justification = "登録を削除しても、待機中の操作が使用するgateは破棄しません。")]
    public async ValueTask<EOutcome> RemoveAsync(string id, string expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedVersion);
        await Groups.InitializeAsync(cancellationToken).ConfigureAwait(false);
        SemaphoreSlim gate = Gate(id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (ownership.Check(id, EProcessGroupAccess.Remove, null) is not null)
            {
                return EOutcome.NotExecuted;
            }
            EOutcome result = await registry.RemoveAsync(id, expectedVersion, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            if (result == EOutcome.Succeeded)
            {
                Interlocked.Increment(ref registrationRevision);
                monitor.Forget(id);
            }
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>登録した管理対象だけを起動します。</summary>
    /// <returns>実起動の結果です。</returns>
    public ValueTask<ProcessOperationResult> StartAsync(string id, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(id, static (session, token) => session.StartAsync(cancellationToken: token), cancellationToken: cancellationToken);
    }

    /// <summary>停止後方針の指定を要求して対象を停止します。</summary>
    /// <returns>実終了または未完了の結果です。</returns>
    public ValueTask<ProcessOperationResult> StopAsync(string id, EProcessStopPolicy policy, int gracefulTimeoutMilliseconds, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(policy) || gracefulTimeoutMilliseconds < 1)
        {
            return ValueTask.FromResult(new ProcessOperationResult(EOutcome.NotExecuted, null, "process.stopPolicyRequired"));
        }
        return ExecuteAsync(id, (session, token) => session.StopAsync(policy, gracefulTimeoutMilliseconds, token), cancellationToken: cancellationToken);
    }

    /// <summary>同じアプリケーションの排他を保持し、終了確認後に一回だけ再起動します。</summary>
    /// <returns>停止に失敗した場合は起動を行わない結果です。</returns>
    public ValueTask<ProcessOperationResult> RestartAsync(string id, EProcessStopPolicy policy, int gracefulTimeoutMilliseconds, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(policy) || gracefulTimeoutMilliseconds < 1)
        {
            return ValueTask.FromResult(new ProcessOperationResult(EOutcome.NotExecuted, null, "process.stopPolicyRequired"));
        }
        return ExecuteAsync(id, async (session, token) =>
        {
            ProcessOperationResult stopped = await session.StopAsync(policy, gracefulTimeoutMilliseconds, token).ConfigureAwait(false);
            return stopped.Outcome == EOutcome.Succeeded ? await session.StartAsync(restarting: true, cancellationToken: token).ConfigureAwait(false)
                                                         : stopped;
        }, cancellationToken: cancellationToken);
    }

    /// <summary>実状態を観測し同じ終了を二重計上しません。</summary>
    /// <returns>故障履歴を含む状態です。</returns>
    public ValueTask<ProcessOperationResult> ObserveAsync(string id, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(id, static (session, token) => session.ObserveAsync(cancellationToken: token), access: EProcessGroupAccess.Observe, cancellationToken: cancellationToken);
    }

    /// <summary>成立した復旧条件を明示限界内で処理します。</summary>
    /// <returns>実復旧または抑止・上限です。</returns>
    public ValueTask<ProcessOperationResult> RecoverAsync(string id, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(id, static (session, token) => session.RecoverAsync(token), automatic: true, access: EProcessGroupAccess.Recover, cancellationToken: cancellationToken);
    }

    /// <summary>起動予定の待機中も監視を続け、許可された時点でだけ自動復旧します。</summary>
    /// <returns>編集で無効化した登録へ古い監視を適用しない結果です。</returns>
    internal ValueTask<ProcessOperationResult> MonitorAsync(RegisteredProcess registration, bool deferRecovery, CancellationToken cancellationToken = default)
    {
        if (deferRecovery)
        {
            return ExecuteAsync(registration.Definition.Id, static (session, token) => session.ObserveAsync(cancellationToken: token), registration.Version, true, EProcessGroupAccess.Observe, cancellationToken: cancellationToken);
        }
        return ExecuteAsync(registration.Definition.Id, static (session, token) => session.RecoverAsync(token), registration.Version, true, EProcessGroupAccess.Recover, cancellationToken: cancellationToken);
    }

    /// <summary>汎用ノードから現起動回の準備・利用可能・安定状態を渡します。</summary>
    /// <returns>起動回を照合した保存結果です。</returns>
    public ValueTask<ProcessOperationResult> SignalAsync(string id, ProcessSignal signal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signal);
        return ExecuteAsync(id, (session, token) => session.SignalAsync(signal, token), access: EProcessGroupAccess.Observe, cancellationToken: cancellationToken);
    }

    /// <summary>人が外部操作前に復旧抑止を保存します。</summary>
    /// <returns>保存済みの状態です。</returns>
    public ValueTask<ProcessOperationResult> SetRecoverySuppressedAsync(string id, bool suppressed, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(id, (session, token) => session.ControlAsync(suppressed, false, token), cancellationToken: cancellationToken);
    }

    /// <summary>人が故障の区切りを確認します。</summary>
    /// <returns>回数を初期化した状態です。</returns>
    public ValueTask<ProcessOperationResult> ConfirmRecoveryAsync(string id, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(id, static (session, token) => session.ControlAsync(null, true, token), cancellationToken: cancellationToken);
    }

    /// <summary>人が現物確認した実起動を紐付け、未確定起動を解決します。</summary>
    /// <returns>明示確認を保存した状態です。</returns>
    public ValueTask<ProcessOperationResult> ResolveLaunchAsync(string id, ProcessIdentity? confirmedIdentity, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(id, (session, token) => session.ResolveLaunchAsync(confirmedIdentity, token), access: EProcessGroupAccess.Observe, cancellationToken: cancellationToken);
    }

    /// <summary>確認した登録版の予定だけを、同じ操作排他内で一回受け付けます。</summary>
    /// <returns>次回予定と現在の確認状態です。</returns>
    internal async ValueTask<ProcessScheduleStatus> RunScheduleAsync(RegisteredProcess registration, ProcessSchedule schedule, DateTimeOffset activatedAt, CancellationToken cancellationToken = default)
    {
        ProcessScheduleStatus? status = null;
        ProcessOperationResult result = await ExecuteAsync(registration.Definition.Id, async (session, token) =>
        {
            ProcessScheduleRunner runner = new(registry.Schedules, clock, boot);
            status = await runner.RunAsync(registration.Definition.Id, schedule, session, activatedAt, token).ConfigureAwait(false);
            return new(status.Outcome ?? EOutcome.Succeeded, null, status.Reason);
        }, registration.Version, true, cancellationToken: cancellationToken).ConfigureAwait(false);
        return status ?? new(schedule.Id, null, Outcome: result.Outcome, Reason: result.Reason);
    }

    /// <summary>起動操作を行わず、登録した予定の次回と直近結果を返します。</summary>
    /// <returns>有限件数の予定表示です。</returns>
    public async IAsyncEnumerable<ProcessScheduleStatus> ReadSchedulesAsync(ProcessDefinition definition, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        foreach (ProcessSchedule schedule in definition.Schedules ?? [])
        {
            (_, ProcessScheduleCursor? cursor) = await registry.Schedules.ReadAsync(definition.Id, schedule.Id, cancellationToken).ConfigureAwait(false);
            if (ProcessSchedulePlan.IsBoot(schedule))
            {
                SystemBootSession? observedBoot = boot();
                ProcessScheduleStatus status = ProcessSchedulePlan.BootStatus(schedule, cursor, observedBoot, clock.GetUtcNow());
                if (status.NextAt is not null && observedBoot is { } observed && !schedule.SkipWhenLoggedOut
                    && TimeSpan.FromMilliseconds(schedule.BootDelayMilliseconds) <= observed.Uptime && !monitor.Platform.IsLaunchUserAvailable(definition))
                {
                    status = status with { Outcome = null, Reason = ProcessSchedulePlan.AwaitingLogin };
                }
                yield return status;
                continue;
            }
            DateTimeOffset? next = null;
            if (schedule.Enabled)
            {
                next = ProcessSchedulePlan.Create(schedule, clock.GetUtcNow()).Plan!.Next(cursor?.After ?? clock.GetUtcNow());
            }
            yield return new(schedule.Id, next, cursor?.ScheduledAt, cursor?.Outcome, cursor?.Reason);
        }
    }

    /// <summary>PC起動の待機中は壁時計補正に左右されず、未ログインの再確認では保存先へ触れません。</summary>
    /// <returns>予定の受付状態を再確認する時機かです。</returns>
    internal bool IsScheduleDue(ProcessDefinition definition, ProcessSchedule schedule, ProcessScheduleStatus status, bool initialRead = false)
    {
        if (!schedule.Enabled || status.NextAt is not { } next)
        {
            return false;
        }
        if (!ProcessSchedulePlan.IsBoot(schedule))
        {
            return next <= clock.GetUtcNow();
        }
        if (boot() is not { } observed)
        {
            return next <= clock.GetUtcNow();
        }
        return TimeSpan.FromMilliseconds(schedule.BootDelayMilliseconds) <= observed.Uptime
            && (initialRead || !string.Equals(status.Reason, ProcessSchedulePlan.AwaitingLogin, StringComparison.Ordinal) || schedule.SkipWhenLoggedOut || monitor.Platform.IsLaunchUserAvailable(definition));
    }

    /// <summary>監視停止後に排他資源を解放し、アプリケーション自体は停止しません。</summary>
    public void Dispose()
    {
        monitor.Dispose();
        Groups.Dispose();
        foreach (SemaphoreSlim gate in gates.Values)
        {
            gate.Dispose();
        }
        gates.Clear();
    }

    /// <summary>同じアプリケーションだけを直列化し他アプリケーションへ待機を波及させません。</summary>
    /// <returns>一件の操作結果です。</returns>
    // 抑制理由: アプリケーションの共有gateはサービスが所有し、個別処理はWaitとReleaseだけを行います。
    [SuppressMessage("SourceQuality", "RAW141", Justification = "service所有の共有gateをここで破棄すると後続操作を壊します。")]
    private async ValueTask<ProcessOperationResult> ExecuteAsync(string id, Func<ProcessSession, CancellationToken, ValueTask<ProcessOperationResult>> action, string? expectedVersion = null, bool automatic = false,
        EProcessGroupAccess access = EProcessGroupAccess.Control, string? group = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        SemaphoreSlim gate = Gate(id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Groups.InitializeAsync(cancellationToken).ConfigureAwait(false);
            if (ownership.Check(id, access, group) is { } blocked)
            {
                return new(EOutcome.NotExecuted, null, blocked);
            }
            if (expectedVersion is not null && !await registry.MatchesAsync(id, expectedVersion, cancellationToken).ConfigureAwait(false))
            {
                return new(EOutcome.NotExecuted, null, "process.registrationChanged");
            }
            ProcessDefinition? definition = await registry.DefinitionAsync(id, cancellationToken).ConfigureAwait(false);
            if (definition is null)
            {
                return new ProcessOperationResult(EOutcome.NotExecuted, null, "process.notRegistered");
            }
            (string? version, ProcessStateRecord state) = await registry.ReadStateAsync(definition, cancellationToken).ConfigureAwait(false);
            ProcessSession session = new(registry, definition, Options, version, state, monitor, replacements, clock, events, automatic);
            return await action(session, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is System.Data.Common.DbException or IOException or JsonException or OverflowException or InvalidOperationException)
        {
            return new ProcessOperationResult(EOutcome.Unknown, null, "process.operationUnconfirmed");
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>グループが確保した登録版だけを、個別操作と同じ排他内で操作します。</summary>
    /// <returns>既存の起動・停止・復旧規則を適用した結果です。</returns>
    internal ValueTask<ProcessOperationResult> ExecuteGroupAsync(string group, ProcessGroupTarget target, EProcessGroupCommand command,
        ProcessGroupDefinition plan, bool automatic, bool recoverOnly = false, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(target.ApplicationId, (session, token) =>
        {
            if (recoverOnly)
            {
                return session.RecoverAsync(token);
            }
            if (command == EProcessGroupCommand.Stop)
            {
                return session.StopAsync(plan.StopPolicy, plan.StopTimeoutMilliseconds, token);
            }
            return session.StartAsync(restarting: automatic || command == EProcessGroupCommand.Restart, recovering: automatic, cancellationToken: token);
        },
            target.RegistrationVersion, automatic, group: group, cancellationToken: cancellationToken);
    }

    /// <summary>アプリケーション専用の排他資源を取得します。</summary>
    /// <returns>サービスが所有し終了時に破棄する排他資源です。</returns>
    private SemaphoreSlim Gate(string id)
        => gates.GetOrAdd(id, static _ => new SemaphoreSlim(1, 1));

    /// <summary>全体停止より前に、再起動先のユーザーが存在するか確認します。</summary>
    /// <returns>現在の起動セッションの利用可否です。</returns>
    internal bool IsGroupLaunchAvailable(ProcessDefinition definition) => monitor.Platform.IsLaunchUserAvailable(definition);
}
