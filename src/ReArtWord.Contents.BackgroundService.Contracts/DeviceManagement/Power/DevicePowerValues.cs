namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Power;

/// <summary>電源供給別の秒単位設定。読取りのnullは未確認、変更のnullは未変更です。</summary>
public sealed record DevicePowerValues(uint? DisplaySeconds = null, uint? SleepSeconds = null, uint? HibernateSeconds = null, uint? PowerButtonAction = null, uint? UnattendedSleepSeconds = null)
{
    /// <summary>公開する設定名と値を一つの順序で扱います。</summary>
    /// <returns>OS操作・検証で共有する項目です。</returns>
    public IReadOnlyDictionary<string, uint?> Items() => new Dictionary<string, uint?>
    {
        [nameof(DisplaySeconds)] = DisplaySeconds,
        [nameof(SleepSeconds)] = SleepSeconds,
        [nameof(HibernateSeconds)] = HibernateSeconds,
        [nameof(PowerButtonAction)] = PowerButtonAction,
        [nameof(UnattendedSleepSeconds)] = UnattendedSleepSeconds,
    };

    /// <summary>秒数と短押し動作を検証し、未確認項目を変更しません。</summary>
    public void Validate(DevicePowerValues? current = null)
    {
        if (DisplaySeconds is > 604800 || SleepSeconds is > 604800 || HibernateSeconds is > 604800 || PowerButtonAction is > 3 || UnattendedSleepSeconds is > 604800)
        {
            throw new ArgumentException("Device.InvalidPowerSetting");
        }
        if (current is not null && Items().Any(item => item.Value is not null && current.Items()[item.Key] is null))
        {
            throw new ArgumentException("Device.PowerSettingUnavailable");
        }
    }

    /// <summary>指定された値だけを反映後の値と照合します。</summary>
    /// <returns>指定項目がすべて一致したかです。</returns>
    public bool Matches(DevicePowerValues actual)
    {
        ArgumentNullException.ThrowIfNull(actual);
        return Items().All(item => item.Value is null || item.Value == actual.Items()[item.Key]);
    }
}
