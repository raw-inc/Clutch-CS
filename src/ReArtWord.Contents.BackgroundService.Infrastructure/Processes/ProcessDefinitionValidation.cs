using ReArtWord.Contents.BackgroundService.Contracts.Processes;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Processes;

/// <summary>登録するアプリケーションと予定の意味を検証します。</summary>
internal static class ProcessDefinitionValidation
{
    /// <summary>実行物の存在を要求せず、起動・復旧・予定の設定を確認します。</summary>
    /// <returns>登録可能な設定の場合はtrueです。</returns>
    internal static bool IsValid(ProcessDefinition definition, TimeProvider clock, Func<SystemBootSession?> boot)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Id);
        ArgumentNullException.ThrowIfNull(definition.Arguments);
        ProcessRecoveryPolicy? recovery = definition.Recovery;
        bool invalid = !Enum.IsDefined(definition.Mode) || string.IsNullOrWhiteSpace(definition.ExecutablePath) || string.IsNullOrWhiteSpace(definition.WorkingDirectory) || definition.Arguments.Any(argument => argument is null) || recovery?.MaximumAttempts is < 0 || recovery?.DelayMilliseconds is < 0 || recovery?.StopTimeoutMilliseconds is <= 0 || definition.ExistingProcess?.ProcessId is <= 0;
        if (definition.Launch is { } launch && (!Enum.IsDefined(launch.Session) || !Enum.IsDefined(launch.Window)))
        {
            invalid = true;
        }
        if (definition.RestartDetection is { } detection && (detection.TimeoutMilliseconds is < 0 or > 3600000 || !Enum.IsDefined(detection.Match)))
        {
            invalid = true;
        }
        if (definition.Mode == EProcessMode.ObserveOnly && definition.ExistingProcess is null)
        {
            invalid = true;
        }
        if (definition.LogicalName is { Length: > 160 } || 32 < definition.Schedules?.Count
            || (definition.Schedules ?? []).Select(schedule => schedule.Id).Distinct(StringComparer.Ordinal).Count() != (definition.Schedules?.Count ?? 0))
        {
            invalid = true;
        }
        if (invalid)
        {
            return false;
        }
        try
        {
            foreach (ProcessSchedule schedule in definition.Schedules ?? [])
            {
                _ = ProcessSchedulePlan.Create(schedule, clock.GetUtcNow());
                if (ProcessSchedulePlan.IsBoot(schedule) && boot() is null)
                {
                    return false;
                }
            }
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
        return true;
    }
}
