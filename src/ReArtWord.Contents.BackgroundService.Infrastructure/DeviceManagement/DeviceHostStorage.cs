namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement;

/// <summary>別クラスタのCoreも共有する、PC設定と変更排他の保存先です。</summary>
internal static class DeviceHostStorage
{
    /// <summary>Unixではシステム資材の領域を避け、管理者が書き込める永続領域を使います。</summary>
    internal static string DirectoryPath
    {
        get
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (OperatingSystem.IsMacOS())
            {
                root = "/Library/Application Support";
            }
            if (OperatingSystem.IsLinux())
            {
                root = "/var/lib";
            }
            return Path.Combine(root, "ReArtWord", "Contents", "host-manager");
        }
    }
}
