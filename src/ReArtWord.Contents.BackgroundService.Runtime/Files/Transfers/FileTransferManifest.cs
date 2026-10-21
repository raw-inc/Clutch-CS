using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;

namespace ReArtWord.Contents.BackgroundService.Runtime.Files.Transfers;

/// <summary>転送の再利用と最終確定に共通の内容digestを使用します。</summary>
public static class FileTransferManifest
{
    /// <summary>OSや列挙順に依存しないマニフェスト digestを作成します。</summary>
    /// <returns>SHA256の大文字hexです。</returns>
    public static string ComputeSha256(IReadOnlyList<FileManifestEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        string json = JsonSerializer.Serialize(entries.OrderBy(entry => entry.RelativePath, StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }
}
