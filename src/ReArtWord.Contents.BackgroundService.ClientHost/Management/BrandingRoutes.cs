using System.Security.Cryptography;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Management;

/// <summary>起動時に指定されたローカル画像だけをサイズ制限付きで配信します。</summary>
internal static class BrandingRoutes
{
    /// <summary>state内のbranding.jsonを読み、任意パスをHTTP入力として受け付けません。</summary>
    public static void Map(WebApplication app, string stateDirectory)
    {
        string path = Path.Combine(stateDirectory, "branding.json");
        ClientBranding settings = new();
        if (File.Exists(path))
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(new FileInfo(path).Length, 16384);
            settings = JsonSerializer.Deserialize<ClientBranding>(File.ReadAllText(path), ContractJson.Options) ?? new();
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.ContentsName);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(settings.ContentsName.Length, ClientBranding.MaximumTextLength);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.Subtitle);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(settings.Subtitle.Length, ClientBranding.MaximumTextLength);
        string? header = MapImage(app, stateDirectory, settings.HeaderLogo, "header");
        string? contents = MapImage(app, stateDirectory, settings.ContentsLogo, "contents");
        string? favicon = MapImage(app, stateDirectory, settings.Favicon, "favicon") ?? header;
        ClientBranding view = new(settings.ContentsName, header, contents, favicon, settings.Subtitle);
        app.MapGet("/client/branding", () => view);
    }

    /// <summary>画像は起動時に固定し、配信中のファイル差替えに依存しません。</summary>
    /// <returns>画像ありの場合だけ同一originのURLです。</returns>
    private static string? MapImage(WebApplication app, string directory, string? configured, string name)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }
        string path = Path.GetFullPath(configured, directory);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(new FileInfo(path).Length, ClientBranding.MaximumImageBytes);
        string mime = Path.GetExtension(path).ToUpperInvariant() switch
        {
            ".PNG" => "image/png",
            ".JPG" or ".JPEG" => "image/jpeg",
            ".WEBP" => "image/webp",
            ".GIF" => "image/gif",
            ".ICO" => "image/x-icon",
            _ => throw new ArgumentException("Brand image must be PNG, JPEG, WebP, GIF or ICO.", nameof(configured)),
        };
        byte[] bytes = File.ReadAllBytes(path);
        string url = "/client/branding/" + name;
        app.MapGet(url, () => Results.Bytes(bytes, mime));
        return url + "?v=" + Convert.ToHexString(SHA256.HashData(bytes));
    }
}
