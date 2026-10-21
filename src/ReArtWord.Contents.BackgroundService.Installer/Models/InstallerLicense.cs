namespace ReArtWord.Contents.BackgroundService.Installer.Models;

/// <summary>ライセンスの選択案内とLGPL v3・GPL v3の原文です。</summary>
internal static class InstallerLicense
{
    /// <summary>配布するLICENSEと同じ原文です。</summary>
    public static string Text
    {
        get;
    } = Read();

    /// <summary>埋め込んだ原文を一度だけ読み込みます。</summary>
    /// <returns>ライセンスの原文です。</returns>
    private static string Read()
    {
        using Stream stream = typeof(InstallerLicense).Assembly.GetManifestResourceStream("Clutch-CS.License") ?? throw new InvalidDataException("Missing LGPL-3.0 license text.");
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }
}
