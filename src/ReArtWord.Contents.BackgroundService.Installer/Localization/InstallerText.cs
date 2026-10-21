using System.Text.Json;

namespace ReArtWord.Contents.BackgroundService.Installer.Localization;

/// <summary>全画面で共通の表示言語を使用します。</summary>
internal sealed class InstallerText
{
    private readonly Dictionary<string, string> values;

    /// <summary>埋め込まれた翻訳を読み込みます。</summary>
    public InstallerText(bool japanese)
    {
        string language = "en";
        if (japanese)
        {
            language = "ja";
        }
        using Stream stream = typeof(InstallerText).Assembly.GetManifestResourceStream("ReArtWord.Contents.BackgroundService.Installer.Localization." + language + ".json") ?? throw new InvalidDataException("Missing installer language resources.");
        values = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }

    /// <summary>画面文言をキーで取得します。</summary>
    /// <param name="key">翻訳キーです。</param>
    /// <returns>表示文言です。</returns>
    public string this[string key] => values[key];
}
