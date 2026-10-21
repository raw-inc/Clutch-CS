using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Processes;

/// <summary>保存を確認した状態変化を一回通知し、自動起動の結果と観測済み終了を運用ログに残します。</summary>
internal sealed class ProcessLifecyclePublisher(string peerId, IWorkflowEvents? events, TimeProvider clock, IWorkflowLog? log)
{
    /// <summary>同じ状態の再観測や起動意図だけでは通知しません。</summary>
    /// <returns>現在の購読者への有限な配信試行です。</returns>
    internal async ValueTask ChangedAsync(ProcessDefinition definition, ProcessStateRecord previous, ProcessStateRecord current, bool automatic, CancellationToken cancellationToken = default)
    {
        string? kind = null;
        if (!current.LaunchPending && current.Identity is not null && previous.LaunchPending && current.InstanceId == previous.InstanceId)
        {
            kind = current.RestartRequested ? "restarted"
                                            : "started";
        }
        if (kind is null && current.RestartRequested && previous.Identity is not null && current.Identity is not null && previous.Identity != current.Identity && !current.LaunchPending)
        {
            kind = "restarted";
        }
        if (kind is null && !current.ExitUnobserved && previous.ExitedAt is null && current.ExitedAt is not null && previous.InstanceId == current.InstanceId)
        {
            kind = "stopped";
        }
        if (kind is null)
        {
            return;
        }
        DateTimeOffset now = clock.GetUtcNow();
        ProcessLifecycleEvent detail = new(definition.Id, definition.DisplayName, kind, current.InstanceId, current.Identity, current.ExitCode, current.PlannedStop, now);
        string stoppedKind = "stopped", restartedKind = "restarted";
        bool stopped = string.Equals(kind, stoppedKind, StringComparison.Ordinal);
        bool automaticTransition = automatic || (string.Equals(kind, restartedKind, StringComparison.Ordinal) && !previous.LaunchPending);
        if (stopped || automaticTransition)
        {
            string level = "INFO";
            if (stopped)
            {
                level = "WARN";
            }
            string message = kind switch { "started" => "Application.Started", "restarted" => "Application.Restarted", _ => "Application.Stopped" };
            await WriteAsync(definition, current, level, message, kind, automaticTransition, EOutcome.Succeeded, null, null, cancellationToken).ConfigureAwait(false);
        }
        if (events is not null)
        {
            await events.PublishAsync(new WorkflowEvent(Guid.NewGuid(), "application." + kind, now, peerId, NodeValue.FromObject(detail)), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>一回の自動起動の失敗を、未実行・結果不明の区別を残して記録します。</summary>
    /// <returns>起動処理の成否へ波及させない記録です。</returns>
    internal ValueTask StartFailedAsync(ProcessDefinition definition, ProcessStateRecord state, bool restarting, EOutcome outcome, string? reason,
        string? exceptionType = null, CancellationToken cancellationToken = default)
    {
        string operation = "start";
        if (restarting)
        {
            operation = "restart";
        }
        return WriteAsync(definition, state, "ERROR", "Application.StartFailed", operation, true, outcome, reason, exceptionType, cancellationToken);
    }

    /// <summary>実行引数やパスを複製せず、登録名・実体・終了コードと失敗理由で追跡できます。</summary>
    /// <returns>ログ障害をアプリケーション操作から分離した受付です。</returns>
    private ValueTask WriteAsync(ProcessDefinition definition, ProcessStateRecord state, string level, string message, string operation,
        bool automatic, EOutcome outcome, string? reason, string? exceptionType, CancellationToken cancellationToken = default)
    {
        if (log is null)
        {
            return ValueTask.CompletedTask;
        }
        return WorkflowLogBoundary.WriteAsync(log, new(Guid.NewGuid(), clock.GetUtcNow(), level, peerId, message,
            Data: ContractJson.ToValue(new { applicationId = definition.Id, applicationName = definition.DisplayName, operation, automatic,
                instanceId = state.InstanceId, pid = state.Identity?.ProcessId, processStartedAt = state.Identity?.StartedAt,
                exitCode = state.ExitCode, plannedStop = state.PlannedStop, recoveryAttempts = state.RecoveryAttempts, outcome, reason, exceptionType })), cancellationToken);
    }
}
