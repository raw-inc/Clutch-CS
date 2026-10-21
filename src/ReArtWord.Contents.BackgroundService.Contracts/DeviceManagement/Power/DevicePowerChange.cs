namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Power;

/// <summary>明示した電源設定だけを更新します。</summary>
public sealed record DevicePowerChange(DevicePowerValues? Ac = null, DevicePowerValues? Dc = null, bool? FastStartup = null,
    uint? IdleShutdownAcSeconds = null, uint? IdleShutdownDcSeconds = null)
{
    /// <summary>未確認や組織ポリシーとの競合を開始前に拒否します。</summary>
    public void Validate(DevicePowerState? current = null)
    {
        if (IdleShutdownAcSeconds is > 604800 || IdleShutdownDcSeconds is > 604800)
        {
            throw new ArgumentException("Device.InvalidPowerSetting");
        }
        if (!(Ac?.Items().Any(item => item.Value is not null) ?? false) && !(Dc?.Items().Any(item => item.Value is not null) ?? false)
            && FastStartup is null && IdleShutdownAcSeconds is null && IdleShutdownDcSeconds is null)
        {
            throw new ArgumentException("Device.NoChanges");
        }
        Ac?.Validate(current?.Ac);
        Dc?.Validate(current?.Dc);
        if (current is null)
        {
            return;
        }
        if (current.Restrictions.Count != 0)
        {
            throw new ArgumentException("Device.PowerManagedPolicy");
        }
        if ((!current.SleepAvailable && (Ac?.SleepSeconds is > 0 || Dc?.SleepSeconds is > 0 || Ac?.PowerButtonAction == 1 || Dc?.PowerButtonAction == 1))
            || (!current.HibernateAvailable && (Ac?.HibernateSeconds is > 0 || Dc?.HibernateSeconds is > 0 || Ac?.PowerButtonAction == 2 || Dc?.PowerButtonAction == 2 || FastStartup == true)))
        {
            throw new ArgumentException("Device.PowerSettingUnavailable");
        }
    }

    /// <summary>省略した項目を比較対象へ含めません。</summary>
    /// <returns>要求どおりの設定を確認できたかです。</returns>
    public bool Matches(DevicePowerState actual)
    {
        ArgumentNullException.ThrowIfNull(actual);
        return (Ac?.Matches(actual.Ac) ?? true) && (Dc?.Matches(actual.Dc) ?? true)
            && (FastStartup is null || FastStartup == actual.FastStartup)
            && (IdleShutdownAcSeconds is null || IdleShutdownAcSeconds == actual.IdleShutdown.AcSeconds)
            && (IdleShutdownDcSeconds is null || IdleShutdownDcSeconds == actual.IdleShutdown.DcSeconds)
            && actual.Restrictions.Count == 0;
    }
}
