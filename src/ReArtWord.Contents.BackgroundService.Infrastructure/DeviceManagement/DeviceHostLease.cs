namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement;

/// <summary>同一PCの別Core・別クラスタからのOS設定変更を直列化します。</summary>
internal static class DeviceHostLease
{
    /// <summary>thread affinityを持たないOSファイル排他を今回の変更へ貸し出します。</summary>
    /// <returns>占有できたlease。別処理が所有する場合はnullです。</returns>
    internal static FileStream? TryAcquire(string directory)
    {
        Directory.CreateDirectory(directory);
        try
        {
            return new(Path.Combine(directory, "settings.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }
}
