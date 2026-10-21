using System.Security.Cryptography;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Runtime.Files.Transactions;

/// <summary>生成物はハッシュを照合し、明示したスキップは既存パス参照として扱います。</summary>
public static class FileArtifactAccess
{
    /// <summary>同Peerの参照だけを受け、生成済みと既存パスの保証を区別します。</summary>
    /// <returns>許可範囲内の入力パスです。</returns>
    public static async Task<string> ResolveInputAsync(string peerId, NodeValue value, FileAccessPolicy policy, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peerId);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(policy);
        string? path = value.GetString();
        string? expected = null;
        if (path is null)
        {
            string peerKey = "peerId";
            string pathKey = "path";
            string hashKey = "sha256";
            if (value.Json.ValueKind != JsonValueKind.Object || !value.Json.TryGetProperty(peerKey, out JsonElement owner) || !string.Equals(owner.GetString(), peerId, StringComparison.Ordinal) || !value.Json.TryGetProperty(pathKey, out JsonElement location))
            {
                throw new InvalidDataException("生成物の所有Peer・path・hashを確認できません。");
            }
            path = location.GetString();
            if (value.Json.TryGetProperty(hashKey, out JsonElement hash))
            {
                expected = hash.GetString();
                ArgumentException.ThrowIfNullOrWhiteSpace(expected);
            }
            else
            {
                string skippedKey = "skipped", reasonKey = "skipReason";
                bool skipped = value.Json.TryGetProperty(skippedKey, out JsonElement marker) && marker.ValueKind == JsonValueKind.True
                    && value.Json.TryGetProperty(reasonKey, out JsonElement reason) && reason.ValueKind == JsonValueKind.String && reason.GetString() is "existing" or "locked";
                if (!skipped)
                {
                    throw new InvalidDataException("生成物のhashまたは既存ファイルのスキップ参照を確認できません。");
                }
            }
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string resolved = policy.ResolvePath(path, true);
        if (expected is not null)
        {
            using FileStream input = new(resolved, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            string actual = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("生成物の現在内容が指定hashと一致しません。");
            }
        }
        return resolved;
    }
}
