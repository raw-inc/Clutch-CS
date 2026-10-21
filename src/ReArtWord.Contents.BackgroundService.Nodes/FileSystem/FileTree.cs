using System.Security.Cryptography;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Runtime.Files;

namespace ReArtWord.Contents.BackgroundService.Nodes.FileSystem;

/// <summary>link非通過のtree操作と容量を制限したバッファーーでの内容検証を共通化します。</summary>
internal static class FileTree
{
    /// <summary>退避内容を復元前に検証するためのマニフェストを作成します。</summary>
    /// <returns>全実体の相対パスと内容digestです。</returns>
    internal static async Task<IReadOnlyList<FileManifestEntry>> ManifestAsync(string path, FileAccessPolicy policy, CancellationToken cancellationToken = default)
    {
        Queue<string> pending = new();
        List<FileManifestEntry> entries = [];
        pending.Enqueue(path);
        while (pending.TryDequeue(out string? current))
        {
            FileEntryInfo info = await StatAsync(current, policy, cancellationToken).ConfigureAwait(false);
            string relative = string.Empty;
            if (!string.Equals(path, current, StringComparison.Ordinal))
            {
                relative = Path.GetRelativePath(path, current).Replace(Path.DirectorySeparatorChar, '/');
            }
            entries.Add(new(relative, info.IsDirectory, info.Length, info.Sha256));
            if (info.IsDirectory)
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(current))
                {
                    pending.Enqueue(entry);
                }
            }
        }
        return entries;
    }

    /// <summary>実体を確認してsizeとSHA256を読みます。</summary>
    /// <returns>内容の同一性です。</returns>
    internal static async Task<FileEntryInfo> StatAsync(string path, FileAccessPolicy policy, CancellationToken cancellationToken = default)
    {
        policy.ValidatePath(path, true);
        if (Directory.Exists(path))
        {
            return new(path, true, 0, string.Empty);
        }
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        policy.ValidatePath(path, true);
        long length = stream.Length;
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        if (length != stream.Length)
        {
            throw new IOException("file.source.changed");
        }
        return new(path, false, length, Convert.ToHexString(hash));
    }

    /// <summary>treeを再帰列挙し、操作前に全linkを拒否します。</summary>
    internal static void ValidateTree(string path, FileAccessPolicy policy)
    {
        Queue<string> pending = new();
        pending.Enqueue(path);
        while (pending.TryDequeue(out string? current))
        {
            policy.ValidatePath(current, true);
            if (Directory.Exists(current))
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(current))
                {
                    pending.Enqueue(entry);
                }
            }
        }
    }

    /// <summary>内容を容量を制限したバッファーーでコピーし、内部退避にも同じ処理を使います。</summary>
    /// <returns>コピー完了です。</returns>
    internal static async Task CopyAsync(string source, string destination, FileAccessPolicy? sourcePolicy, FileAccessPolicy? destinationPolicy, CancellationToken cancellationToken = default)
    {
        Queue<(string Source, string Destination)> pending = new();
        pending.Enqueue((source, destination));
        while (pending.TryDequeue(out (string Source, string Destination) item))
        {
            cancellationToken.ThrowIfCancellationRequested();
            sourcePolicy?.ValidatePath(item.Source, true);
            destinationPolicy?.ValidatePath(item.Destination);
            if ((File.GetAttributes(item.Source) & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException("linkを含むtreeは操作できません。");
            }
            if (Directory.Exists(item.Source))
            {
                Directory.CreateDirectory(item.Destination);
                foreach (string entry in Directory.EnumerateFileSystemEntries(item.Source))
                {
                    pending.Enqueue((entry, Path.Combine(item.Destination, Path.GetFileName(entry))));
                }
                continue;
            }
            using FileStream input = new(item.Source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            sourcePolicy?.ValidatePath(item.Source, true);
            destinationPolicy?.ValidatePath(item.Destination);
            using FileStream output = new(item.Destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough);
            destinationPolicy?.ValidatePath(item.Destination);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>検証した実体だけを削除します。</summary>
    internal static void Delete(string path, FileAccessPolicy? policy, bool recursive)
    {
        policy?.ValidatePath(path);
        if (Directory.Exists(path))
        {
            if (policy is not null)
            {
                ValidateTree(path, policy);
            }
            Directory.Delete(path, recursive);
        }
        else
        {
            File.Delete(path);
        }
    }
}
