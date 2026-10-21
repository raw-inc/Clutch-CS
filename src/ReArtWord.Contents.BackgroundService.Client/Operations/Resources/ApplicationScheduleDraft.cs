using System.Globalization;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;

namespace ReArtWord.Contents.BackgroundService.Client.Operations.Resources;

/// <summary>日時またはPC起動を基準にした起動予定を編集します。</summary>
internal sealed record ApplicationScheduleDraft
{
    private readonly string id;

    /// <summary>既存予定のIDを維持し、既定では起動済みアプリをスキップします。</summary>
    internal ApplicationScheduleDraft(ProcessSchedule? source = null)
    {
        id = source?.Id ?? Guid.NewGuid().ToString("N");
        if (source is not null)
        {
            Enabled = source.Enabled;
            Mode = source.Mode;
            Zone = source.TimeZoneId;
            At = source.At ?? string.Empty;
            Time = source.Time ?? "09:00";
            Days.UnionWith(source.Weekdays ?? []);
            Running = source.AlreadyRunning;
            StopPolicy = source.StopPolicy ?? EProcessStopPolicy.WaitForManual;
            StopTimeout = source.StopTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture);
            BootDelay = source.BootDelayMilliseconds.ToString(CultureInfo.InvariantCulture);
            WaitForLogin = !source.SkipWhenLoggedOut;
        }
    }

    internal string EditorId => id;

    internal bool Enabled
    {
        get;
        set;
    } = true;

    internal string Mode
    {
        get;
        set;
    } = "daily";

    internal string Zone
    {
        get;
        set;
    } = "UTC";

    internal string At
    {
        get;
        set;
    } = string.Empty;

    internal string Time
    {
        get;
        set;
    } = "09:00";

    internal EProcessRunningPolicy Running
    {
        get;
        set;
    } = EProcessRunningPolicy.Skip;

    internal EProcessStopPolicy StopPolicy
    {
        get;
        set;
    } = EProcessStopPolicy.WaitForManual;

    internal string StopTimeout
    {
        get;
        set;
    } = "30000";

    internal HashSet<int> Days
    {
        get;
    } = [];

    internal string BootDelay
    {
        get;
        set;
    } = "0";

    internal bool WaitForLogin
    {
        get;
        set;
    } = true;

    /// <summary>暦の検証をCoreの共通実装に委ね、編集値を契約へ写します。</summary>
    /// <returns>保存先で日時とタイムゾーンを再検証する予定です。</returns>
    internal ProcessSchedule Build()
    {
        int delay = 0;
        string bootMode = "boot";
        if (string.Equals(Mode, bootMode, StringComparison.Ordinal))
        {
            delay = FormInput.Integer(BootDelay, "Delay after PC startup (ms)", 0, 86400000);
        }
        return new(id, Mode, Zone, At, Time, Days.Order().ToArray(), Running, StopPolicy,
            FormInput.Integer(StopTimeout, "Graceful stop timeout (ms)", 1, 3600000), Enabled, delay,
            string.Equals(Mode, bootMode, StringComparison.Ordinal) && !WaitForLogin);
    }
}
