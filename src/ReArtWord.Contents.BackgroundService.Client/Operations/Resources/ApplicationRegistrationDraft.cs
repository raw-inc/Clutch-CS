using System.Globalization;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;

namespace ReArtWord.Contents.BackgroundService.Client.Operations.Resources;

/// <summary>アプリケーション登録フォームを既存IDを維持した保存定義へ変換します。</summary>
internal sealed record ApplicationRegistrationDraft
{
    private readonly ProcessDefinition original;

    /// <summary>未保存の編集値を永続定義から分離します。</summary>
    internal ApplicationRegistrationDraft(ProcessDefinition? definition)
    {
        original = definition ?? new("application-" + Guid.NewGuid().ToString("N"), EProcessMode.Managed, string.Empty, [], string.Empty, Launch: new());
        Name = definition?.DisplayName ?? string.Empty;
        Executable = original.ExecutablePath;
        Directory = original.WorkingDirectory;
        Mode = original.Mode;
        LaunchSession = original.Launch?.Session ?? EProcessLaunchSession.CoreAccount;
        WindowMode = original.Launch?.Window ?? EProcessWindowMode.NoWindow;
        Automatic = original.AutomaticMonitoring;
        RestartWait = (original.RestartDetection?.TimeoutMilliseconds ?? 0).ToString(CultureInfo.InvariantCulture);
        RestartMatch = original.RestartDetection?.Match ?? EProcessRestartMatch.ExecutableAndArguments;
        Arguments.AddRange(original.Arguments.Select(value => new ApplicationArgument(value)));
        Schedules.AddRange((original.Schedules ?? []).Select(value => new ApplicationScheduleDraft(value)));
        if (definition is not null)
        {
            ProcessRecoveryPolicy recovery = original.Recovery ?? new();
            NormalExit = recovery.RestartOnNormalExit;
            AbnormalExit = recovery.RestartOnAbnormalExit;
            UnknownExit = recovery.RestartOnUnknownExit;
            Unlimited = recovery.Unlimited;
            Attempts = recovery.MaximumAttempts?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            Delay = recovery.DelayMilliseconds?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            Unhealthy = recovery.RecoverWhenUnhealthy;
            StopPolicy = recovery.UnhealthyStopPolicy?.ToString() ?? string.Empty;
            StopTimeout = recovery.StopTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture);
        }
        if (original.ExistingProcess is { } identity)
        {
            Pid = identity.ProcessId.ToString(CultureInfo.InvariantCulture);
            StartedAt = identity.StartedAt.ToString("O", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>参照互換性を維持する登録識別子です。</summary>
    internal string Id => original.Id;

    internal string Name
    {
        get;
        set;
    } = string.Empty;

    internal string Executable
    {
        get;
        set;
    } = string.Empty;

    internal string Directory
    {
        get;
        set;
    } = string.Empty;

    internal EProcessMode Mode
    {
        get;
        set;
    } = EProcessMode.Managed;

    internal bool Automatic
    {
        get;
        set;
    }

    internal EProcessLaunchSession LaunchSession
    {
        get; set;
    }

    internal EProcessWindowMode WindowMode
    {
        get; set;
    }

    internal bool NormalExit
    {
        get;
        set;
    }

    internal bool AbnormalExit
    {
        get;
        set;
    } = true;

    internal bool UnknownExit
    {
        get;
        set;
    }

    internal bool Unlimited
    {
        get;
        set;
    }

    internal string Attempts
    {
        get;
        set;
    } = "3";

    internal string Delay
    {
        get;
        set;
    } = "1000";

    internal bool Unhealthy
    {
        get;
        set;
    }

    internal string StopPolicy
    {
        get;
        set;
    } = string.Empty;

    internal string StopTimeout
    {
        get;
        set;
    } = "30000";

    internal string Pid
    {
        get;
        set;
    } = string.Empty;

    internal string StartedAt
    {
        get;
        set;
    } = string.Empty;

    internal string RestartWait
    {
        get;
        set;
    } = "0";

    internal EProcessRestartMatch RestartMatch
    {
        get;
        set;
    }

    internal List<ApplicationArgument> Arguments
    {
        get;
    } = [];

    internal List<ApplicationScheduleDraft> Schedules
    {
        get;
    } = [];

    /// <summary>JSON手入力を要求せず、対象・引数・再起動方針を明示値へ変換します。</summary>
    /// <returns>元IDを維持する保存定義です。</returns>
    internal ProcessDefinition Build()
    {
        if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Executable) || string.IsNullOrWhiteSpace(Directory))
        {
            throw UiError.InvalidOperation("Enter the application name, executable path and working directory.");
        }
        ProcessIdentity? identity = null;
        if (!string.IsNullOrWhiteSpace(Pid))
        {
            int pid = FormInput.Integer(Pid, "PID", 1);
            string offset = "(Z|[+-]\\d\\d:\\d\\d)$";
            if (!DateTimeOffset.TryParse(StartedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset start) || !System.Text.RegularExpressions.Regex.IsMatch(StartedAt, offset))
            {
                throw UiError.InvalidOperation("Enter the exact OS start time including its time zone.");
            }
            identity = new(pid, start);
        }
        if (Mode == EProcessMode.ObserveOnly && identity is null)
        {
            throw UiError.InvalidOperation("Observation-only registration requires the existing PID and OS start time.");
        }
        int? attempts = null, delay = null;
        if (!string.IsNullOrWhiteSpace(Attempts))
        {
            attempts = FormInput.Integer(Attempts, "Maximum restart attempts", 0);
        }
        if (!string.IsNullOrWhiteSpace(Delay))
        {
            delay = FormInput.Integer(Delay, "Restart delay (ms)", 0);
        }
        EProcessStopPolicy? policy = null;
        if (Enum.TryParse(StopPolicy, out EProcessStopPolicy parsed))
        {
            policy = parsed;
        }
        if (Unhealthy && policy is null)
        {
            throw UiError.InvalidOperation("Choose a stop policy for unhealthy application recovery.");
        }
        ProcessLaunchOptions? launch = new(LaunchSession, WindowMode);
        if (original.Launch is null && LaunchSession == EProcessLaunchSession.CoreAccount && WindowMode == EProcessWindowMode.NoWindow)
        {
            // LEGACY-FORMAT: application-launch-unspecified — 別項目だけの編集では従来の起動アカウントと初期表示を変えません。
            launch = null;
        }
        return original with
        {
            LogicalName = Name.Trim(), ExecutablePath = Executable, WorkingDirectory = Directory, Mode = Mode,
            Arguments = Arguments.Select(argument => argument.Value).ToArray(), ExistingProcess = identity, AutomaticMonitoring = Automatic,
            Recovery = new(attempts, Unlimited, delay, Unhealthy, policy, FormInput.Integer(StopTimeout, "Graceful stop timeout (ms)", 1, 3600000), NormalExit, AbnormalExit, UnknownExit),
            RestartDetection = new(FormInput.Integer(RestartWait, "Self-restart confirmation timeout (ms)", 0, 3600000), RestartMatch),
            Schedules = Schedules.Select(schedule => schedule.Build()).ToArray(),
            Launch = launch,
        };
    }
}
