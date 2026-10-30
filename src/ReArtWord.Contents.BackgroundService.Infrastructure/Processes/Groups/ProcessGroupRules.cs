using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Processes.Groups;

/// <summary>グループ登録と、自動復旧を開始できる条件を判定します。</summary>
internal static class ProcessGroupRules
{
    /// <summary>同じPCの管理対象を、重複せず有限数まとめられるか確認します。</summary>
    /// <returns>不正な場合の理由です。</returns>
    internal static string? Validate(ProcessGroupDefinition definition, IReadOnlyList<RegisteredProcess> applications)
    {
        if (string.IsNullOrWhiteSpace(definition.Id) || 160 < definition.Id.Length || string.IsNullOrWhiteSpace(definition.Name) || 160 < definition.Name.Length
            || definition.Members is null || definition.Members.Count is < 2 or > 32
            || !Enum.IsDefined(definition.RecoveryMode) || !Enum.IsDefined(definition.StopPolicy)
            || definition.RecoveryDelayMilliseconds is < 0 or > 3600000 || definition.StopTimeoutMilliseconds is < 1 or > 3600000)
        {
            return "processGroup.invalidDefinition";
        }
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (ProcessGroupMember member in definition.Members)
        {
            if (member is null || string.IsNullOrWhiteSpace(member.ApplicationId) || !ids.Add(member.ApplicationId)
                || member.DelayMilliseconds is < 0 or > 3600000
                || !applications.Any(application => string.Equals(application.Definition.Id, member.ApplicationId, StringComparison.Ordinal) && application.Definition.Mode == EProcessMode.Managed))
            {
                return "processGroup.invalidMember";
            }
        }
        return null;
    }

    /// <summary>個別復旧と同じ終了コード・上限・明示抑止を維持します。</summary>
    /// <returns>全体復旧のきっかけにできる、確認済みの障害かです。</returns>
    internal static bool CanRecover(ProcessDefinition definition, ProcessStatus status, ProcessOptions options)
    {
        if (status.Identity is null || status.RecoverySuppressed || status.PlannedStop || status.RecoveryExhausted || status.ExitUnobserved
            || status.Running is null || status.ReplacementDeadline is not null || status.ReplacementProblem is not null)
        {
            return false;
        }
        ProcessRecoveryPolicy policy = definition.Recovery ?? new();
        int? maximum = policy.MaximumAttempts ?? options.DefaultRecoveryAttempts;
        if (!policy.Unlimited && maximum.HasValue && maximum.Value <= status.RecoveryAttempts)
        {
            return false;
        }
        if (status.Running == true)
        {
            return policy.RecoverWhenUnhealthy && status.Healthy == false;
        }
        return status.ExitCode switch { null => policy.RestartOnUnknownExit, 0 => policy.RestartOnNormalExit, _ => policy.RestartOnAbnormalExit };
    }

    /// <summary>PC起動予定より前に、前回の故障からグループ全体を起動しないようにします。</summary>
    /// <returns>最初の起動予定が未受付の場合はtrueです。</returns>
    internal static async ValueTask<bool> AwaitingBootAsync(ProcessService service, ProcessDefinition definition, CancellationToken cancellationToken = default)
    {
        HashSet<string> boot = (definition.Schedules ?? []).Where(schedule => schedule.Enabled && ProcessSchedulePlan.IsBoot(schedule)).Select(schedule => schedule.Id).ToHashSet(StringComparer.Ordinal);
        if (boot.Count == 0)
        {
            return false;
        }
        bool pending = false;
        await foreach (ProcessScheduleStatus status in service.ReadSchedulesAsync(definition, cancellationToken).ConfigureAwait(false))
        {
            if (!boot.Contains(status.Id))
            {
                continue;
            }
            if (status.NextAt is null && status.Outcome.HasValue && status.Outcome != EOutcome.Unknown)
            {
                return false;
            }
            pending |= status.NextAt.HasValue || status.Outcome == EOutcome.Unknown;
        }
        return pending;
    }
}
