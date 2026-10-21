using System.Security.Cryptography;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting.Runtimes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Scripting.Runtimes;

/// <summary>旧綴りの管理済みruntimeを移行します。旧配置は固定済みvenv等の互換参照がなくなるまで保持します。</summary>
internal static class LegacyScriptRuntimeMigration
{
    /// <summary>現在ログイン中の利用者だけを対象に、検証後に新配置を原子的に公開します。</summary>
    /// <returns>公開を確認した実行先対応と、再試行が必要な理由です。</returns>
    internal static async Task<(IReadOnlyDictionary<string, string> Paths, string? Problem)> MigrateAsync(ScriptRuntimeUser user, string language,
        ScriptRuntimeConfiguration configuration, CancellationToken cancellationToken = default)
    {
        Dictionary<string, string> migrated = new(ScriptRuntimePaths.Comparer);
        string leaf = ScriptRuntimePaths.Language(language) switch { "python" => "py", _ => "js" };
        string legacy = Path.Combine(Path.GetFullPath(user.HomeDirectory), ".clitch-cs", "runtime", leaf);
        if (!Directory.Exists(legacy))
        {
            return (migrated, null);
        }
        string destinationRoot = ScriptRuntimePaths.InstallRoot(user, language);
        try
        {
            ScriptRuntimeArchive.EnsureRoot(user, legacy);
            ScriptRuntimeArchive.EnsureRoot(user, destinationRoot);
            using FileStream previousLock = new(Path.Combine(legacy, ".install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            using FileStream currentLock = new(Path.Combine(destinationRoot, ".install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            foreach (string source in Directory.EnumerateDirectories(legacy).Take(64))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RejectLink(source);
                ScriptRuntimeRelease? release = ScriptRecordFile.Read<ScriptRuntimeRelease>(Path.Combine(source, ".clutch-runtime.json"));
                if (release is null || !string.Equals(release.Language, language, StringComparison.Ordinal))
                {
                    continue;
                }
                string expectedDirectory = release.Version + "-" + release.Platform;
                if (!ScriptRuntimePaths.Comparer.Equals(Path.GetFileName(source), expectedDirectory))
                {
                    continue;
                }
                string executable = language switch { "python" => "python.exe", _ => "node.exe" };
                string previous = Path.Combine(source, executable);
                string destination = Path.Combine(destinationRoot, Path.GetFileName(source));
                string current = Path.Combine(destination, executable);
                if (!File.Exists(previous))
                {
                    continue;
                }
                if (!ScriptRuntimePaths.Comparer.Equals(configuration.Resolve(previous), current) || !File.Exists(current))
                {
                    await CopyAsync(source, destination, destinationRoot, cancellationToken).ConfigureAwait(false);
                }
                migrated[previous] = current;
            }
            return (migrated, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
        {
            return (migrated, "script.runtime.migrationPending");
        }
    }

    /// <summary>既存の移行先を置換せず、同一内容かどうかもファイル単位で照合します。</summary>
    /// <returns>全内容を確認した移行先の公開です。</returns>
    private static async Task CopyAsync(string source, string destination, string parent, CancellationToken cancellationToken = default)
    {
        bool exists = Directory.Exists(destination);
        string staging = Path.Combine(parent, ".migrate-" + Guid.NewGuid().ToString("N"));
        string targetRoot = destination;
        if (!exists)
        {
            Directory.CreateDirectory(staging);
            targetRoot = staging;
        }
        try
        {
            IReadOnlyList<string> files = Entries(source);
            if (exists && !files.ToHashSet(ScriptRuntimePaths.Comparer).SetEquals(Entries(destination)))
            {
                throw new IOException("script.runtime.migrationConflict");
            }
            foreach (string relative in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string from = Path.Combine(source, relative), to = Path.Combine(targetRoot, relative);
                if (Directory.Exists(from))
                {
                    if (!exists)
                    {
                        Directory.CreateDirectory(to);
                    }
                    else
                    {
                        if (!Directory.Exists(to))
                        {
                            throw new IOException("script.runtime.migrationConflict");
                        }
                    }
                    continue;
                }
                if (!exists)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    using FileStream input = new(from, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    using FileStream output = new(to, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                if (!string.Equals(await HashAsync(from, cancellationToken).ConfigureAwait(false), await HashAsync(to, cancellationToken).ConfigureAwait(false), StringComparison.Ordinal))
                {
                    throw new IOException("script.runtime.migrationConflict");
                }
            }
            if (!exists)
            {
                Directory.Move(staging, destination);
            }
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                try
                {
                    Directory.Delete(staging, true);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // この移行専用の未公開領域だけを残し、次回は別の領域で再試行します。
                }
            }
        }
    }

    /// <summary>リンクを辿らず、公式配布と同じ上限で空ディレクトリも含めて列挙します。</summary>
    /// <returns>ルート内であることを確認した通常の子要素です。</returns>
    private static List<string> Entries(string root)
    {
        List<string> files = [];
        Queue<string> directories = new([root]);
        long bytes = 0;
        int entries = 0;
        while (directories.TryDequeue(out string? directory))
        {
            RejectLink(directory);
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                RejectLink(path);
                if (20000 < ++entries)
                {
                    throw new IOException("script.runtime.archiveLimit");
                }
                files.Add(Path.GetRelativePath(root, path));
                if (Directory.Exists(path))
                {
                    directories.Enqueue(path);
                }
                else
                {
                    bytes += new FileInfo(path).Length;
                    if (1073741824 < bytes)
                    {
                        throw new IOException("script.runtime.archiveLimit");
                    }
                }
            }
        }
        return files;
    }

    /// <summary>通常配置だけを扱い、利用者ホーム外へ向いた再解析ポイントを拒否します。</summary>
    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("script.runtime.installPathRedirected");
        }
    }

    /// <summary>内容全体をメモリへ保持せず、移行元と移行先を照合します。</summary>
    /// <returns>通常ファイルのSHA-256です。</returns>
    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken = default)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }
}
