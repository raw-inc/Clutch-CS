using System.IO.Compression;
using System.Security.Cryptography;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting.Runtimes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Scripting.Runtimes;

/// <summary>実行環境を有限サイズで取得し、ハッシュ検証後だけ専用領域へ展開します。</summary>
internal static class ScriptRuntimeArchive
{
    /// <summary>バイナリーをメモリへ一括保持せず、取得と同時にSHA-256を計算します。</summary>
    /// <returns>取得とハッシュ一致の確認です。</returns>
    internal static async Task DownloadAsync(HttpClient client, ScriptRuntimeRelease release, string path, Action<long, long?> progress, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await client.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;
        long maximum = 268435456;
        if (maximum < total)
        {
            throw new InvalidDataException("script.runtime.downloadLimit");
        }
        using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536];
        long received = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            received += count;
            if (maximum < received)
            {
                throw new InvalidDataException("script.runtime.downloadLimit");
            }
            hash.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            progress(received, total);
        }
        if (total is not null && received != total || !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), release.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("script.runtime.checksumMismatch");
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>絶対パス、親参照、リンク、重複名、過大な展開を拒否します。</summary>
    /// <returns>一回専用のステージ領域への展開です。</returns>
    internal static async Task ExtractAsync(string archive, string directory, ScriptRuntimeRelease release, CancellationToken cancellationToken = default)
    {
        using ZipArchive zip = await ZipFile.OpenReadAsync(archive, cancellationToken).ConfigureAwait(false);
        if (20000 < zip.Entries.Count)
        {
            throw new InvalidDataException("script.runtime.archiveLimit");
        }
        string root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        string prefix = release.Language switch { "javascript" => "node-v" + release.Version + "-" + release.Platform + "/", _ => string.Empty };
        long expanded = 0;
        byte[] buffer = new byte[65536];
        string parent = "..", current = ".";
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = entry.FullName.Replace('\\', '/');
            if (name.StartsWith('/') || name.Contains(':', StringComparison.Ordinal) || name.Split('/').Any(part => string.Equals(part, parent, StringComparison.Ordinal) || string.Equals(part, current, StringComparison.Ordinal)) || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000
                || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || !name.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new InvalidDataException("script.runtime.archivePath");
            }
            name = name[prefix.Length..];
            if (name.Length == 0)
            {
                continue;
            }
            string destination = Path.GetFullPath(Path.Combine(directory, name));
            StringComparison comparison = StringComparison.Ordinal;
            if (OperatingSystem.IsWindows())
            {
                comparison = StringComparison.OrdinalIgnoreCase;
            }
            if (!destination.StartsWith(root, comparison))
            {
                throw new InvalidDataException("script.runtime.archivePath");
            }
            if (name.EndsWith('/'))
            {
                Directory.CreateDirectory(destination);
                continue;
            }
            if (1073741824 < expanded + entry.Length)
            {
                throw new InvalidDataException("script.runtime.archiveLimit");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using Stream source = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
            using FileStream target = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
            long written = 0;
            int count;
            while ((count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                written += count;
                expanded += count;
                if (entry.Length < written || 1073741824 < expanded)
                {
                    throw new InvalidDataException("script.runtime.archiveLimit");
                }
                await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>導入先配下のジャンクション等を拒否し、別領域への暗黙の配置を防ぎます。</summary>
    internal static void EnsureRoot(ScriptRuntimeUser user, string root)
    {
        string home = Path.GetFullPath(user.HomeDirectory);
        string path = root;
        while (!ScriptRuntimePaths.Comparer.Equals(path, home))
        {
            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("script.runtime.installPathRedirected");
            }
            path = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("script.runtime.installPathInvalid");
        }
        Directory.CreateDirectory(root);
    }
}
