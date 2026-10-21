namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.WindowsUpdate;

/// <summary>手動更新だけを許可するか、自動更新の制限を解除します。</summary>
public sealed record DeviceWindowsUpdateChange([property: System.Text.Json.Serialization.JsonRequired] bool ManualOnly)
{
    /// <summary>対応エディションと管理元を確認します。</summary>
    public static void Validate(DeviceWindowsUpdateState current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!current.Supported)
        {
            throw new ArgumentException("Device.UpdateEditionUnsupported");
        }
        if (current.Restrictions.Count != 0)
        {
            throw new ArgumentException("Device.UpdateManagedPolicy");
        }
    }

    /// <summary>有効な自動更新設定と、優先される期限ポリシーの状態を確認します。</summary>
    /// <returns>要求されたポリシーを確認できたかです。</returns>
    public bool Matches(DeviceWindowsUpdateState current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!current.Supported || current.Restrictions.Count != 0)
        {
            return false;
        }
        return ManualOnly ? current.NoAutoUpdate == 1 && current.Deadlines.Values.All(value => value is null or 0)
                          : current.NoAutoUpdate is null;
    }
}
