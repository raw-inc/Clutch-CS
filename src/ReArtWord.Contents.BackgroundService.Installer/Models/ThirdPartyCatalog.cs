using System.Text.Json;

namespace ReArtWord.Contents.BackgroundService.Installer.Models;

/// <summary>配布時に作成したライセンス一覧です。</summary>
/// <param name="Installer">インストーラーの依存ライブラリです。</param>
/// <param name="ClutchCs">Clutch-CSの依存ライブラリです。</param>
internal sealed record ThirdPartyCatalog(ThirdPartyLicense[] Installer, ThirdPartyLicense[] ClutchCs)
{
    /// <summary>配布物に同梱した一覧を読み込みます。</summary>
    /// <returns>依存ライブラリのライセンス一覧です。</returns>
    public static ThirdPartyCatalog Read()
    {
        using Stream stream = typeof(ThirdPartyCatalog).Assembly.GetManifestResourceStream("Clutch-CS.ThirdPartyLicenses") ?? throw new InvalidDataException("Missing third-party license catalog.");
        return JsonSerializer.Deserialize<ThirdPartyCatalog>(stream) ?? throw new InvalidDataException("Invalid third-party license catalog.");
    }
}
