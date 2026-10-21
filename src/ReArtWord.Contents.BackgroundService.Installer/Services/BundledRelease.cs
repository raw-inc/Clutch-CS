namespace ReArtWord.Contents.BackgroundService.Installer.Services;

/// <summary>配布アプリと同梱されたreleaseを解決します。</summary>
internal static class BundledRelease
{
    /// <summary>利用者へfolder入力を要求せず、配布構造から場所を取得します。</summary>
    /// <returns>配布rootです。</returns>
    public static string Root()
    {
        string current = AppContext.BaseDirectory;
        string component = "core";
        if (Directory.Exists(Path.Combine(current, component)))
        {
            return current;
        }
        string parent = Path.GetFullPath(Path.Combine(current, ".."));
        if (Directory.Exists(Path.Combine(parent, component)))
        {
            return parent;
        }
        return current;
    }
}
