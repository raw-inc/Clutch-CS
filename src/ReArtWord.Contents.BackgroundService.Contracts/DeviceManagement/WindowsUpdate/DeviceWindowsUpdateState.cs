namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.WindowsUpdate;

/// <summary>Windows Updateの自動実行ポリシーと、既に進行した更新を区別します。</summary>
public sealed record DeviceWindowsUpdateState(string Edition, bool Supported, int? NoAutoUpdate, IReadOnlyDictionary<string, int?> Deadlines,
    IReadOnlyList<string> Restrictions, bool RebootRequired, bool? InstallInProgress);
