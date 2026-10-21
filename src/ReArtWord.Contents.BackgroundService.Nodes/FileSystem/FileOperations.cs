using System.Text;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.FileSystem;

/// <summary>許可範囲と前提を操作直前に確認してローカル実体を操作します。</summary>
internal static class FileOperations
{
    /// <summary>一件の読取りまたは変更を実行します。</summary>
    /// <returns>取得値または操作したパスです。</returns>
    internal static async Task<NodeValue> ExecuteAsync(FileOperationPlan plan, NodeExecutionContext context, FileAccessPolicy policy, CancellationToken cancellationToken = default)
    {
        NodeSettings settings = new(context.Settings);
        string path = policy.ResolvePath(plan.Path, !plan.Scopes[0].Write);
        cancellationToken.ThrowIfCancellationRequested();
        switch (plan.Operation)
        {
            case "file.exists":
                return NodeValue.FromObject(File.Exists(path));
            case "directory.exists":
                return NodeValue.FromObject(Directory.Exists(path));
            case "file.read":
                return await ReadAsync(path, settings.ReadInt32("maxBytes", 1048576), policy, cancellationToken).ConfigureAwait(false);
            case "directory.list":
                return List(path, settings.ReadBoolean("recursive"), settings.ReadInt32("maxEntries", 10000), policy);
            case "file.create":
            case "file.replace":
            case "file.upsert":
            case "file.append":
                if (await WriteAsync(plan, context, policy, cancellationToken).ConfigureAwait(false) is { } written)
                {
                    return written;
                }
                break;
            case "directory.create":

                // 新しく作る親階層も排他・復元範囲に含め、予約外の祖先が消えた場合は中止します。
                RequireParent(plan.Scopes[0].Path);
                RequireAbsent(path);
                Directory.CreateDirectory(path);
                break;
            case "file.delete":
                RequireFileOrMissing(path, settings.ReadBoolean("missingOk"));
                File.Delete(path);
                break;
            case "directory.delete":
                if (!Directory.Exists(path))
                {
                    bool missingOk = settings.ReadBoolean("missingOk");
                    if (!missingOk || File.Exists(path))
                    {
                        throw new DirectoryNotFoundException("削除対象ディレクトリがありません。");
                    }
                    break;
                }
                FileTree.Delete(path, policy, settings.ReadBoolean("recursive"));
                break;
            default:
                if (await RelocateAsync(plan, context, policy, cancellationToken).ConfigureAwait(false) is { } relocated)
                {
                    return relocated;
                }
                break;
        }
        return NodeValue.FromObject(new { path, destination = plan.Destination });
    }

    /// <summary>明示上限を超える読取りをメモリ確保前に拒否します。</summary>
    /// <returns>独立所有するバイト列です。</returns>
    private static async Task<NodeValue> ReadAsync(string path, int maximum, FileAccessPolicy policy, CancellationToken cancellationToken = default)
    {
        if (maximum is < 1 or > 16777216)
        {
            throw new IOException("file.read.maxBytes.invalid");
        }
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        policy.ValidatePath(path, true);
        if (maximum < stream.Length)
        {
            throw new IOException("file.read.tooLarge");
        }
        byte[] bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (stream.Length != bytes.Length)
        {
            throw new IOException("file.source.changed");
        }
        return NodeValue.FromBytes(bytes);
    }

    /// <summary>有限件数でtreeを列挙し、linkを辿りません。</summary>
    /// <returns>パス・種類・sizeの一覧です。</returns>
    private static NodeValue List(string path, bool recursive, int maximum, FileAccessPolicy policy)
    {
        List<object> entries = [];
        Queue<string> directories = new();
        directories.Enqueue(path);
        while (directories.TryDequeue(out string? directory))
        {
            policy.ValidatePath(directory, true);
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                policy.ValidatePath(entry, true);
                if (maximum <= entries.Count)
                {
                    throw new IOException("file.list.tooManyEntries");
                }
                bool isDirectory = Directory.Exists(entry);
                long length = 0;
                if (!isDirectory)
                {
                    length = new FileInfo(entry).Length;
                }
                entries.Add(new { path = entry, isDirectory, length });
                if (recursive && isDirectory)
                {
                    directories.Enqueue(entry);
                }
            }
        }
        return NodeValue.FromObject(entries);
    }

    /// <summary>各操作固有の存在前提を保持して書き込みます。</summary>
    /// <returns>スキップ値、通常書込みの完了はnullです。</returns>
    private static async Task<NodeValue?> WriteAsync(FileOperationPlan plan, NodeExecutionContext context, FileAccessPolicy policy, CancellationToken cancellationToken = default)
    {
        bool requiresExisting = plan.Operation is "file.replace" or "file.append";
        if (requiresExisting)
        {
            RequireFileOrMissing(plan.Path, false);
        }
        FileOutputPolicy.EnsureParent(policy, plan.Path);
        NodeValue value = context.Inputs["value"];
        ReadOnlyMemory<byte> bytes;
        if (value.Kind == EValueKind.Binary)
        {
            bytes = value.Binary;
        }
        else
        {
            bytes = Encoding.UTF8.GetBytes(value.GetString() ?? value.Json.GetRawText());
        }
        string existing = FileOutputPolicy.ExistingMode(new(context.Settings));
        bool overwrite = existing is "overwrite";
        FileMode create = overwrite ? FileMode.OpenOrCreate
                                    : FileMode.CreateNew;
        FileMode mode = plan.Operation switch
        {
            "file.create" => create,
            "file.replace" => FileMode.Open,
            "file.upsert" => create,
            "file.append" => FileMode.Open,
            _ => throw new InvalidOperationException("未対応の書込み操作です。"),
        };
        policy.ValidatePath(plan.Path);
        try
        {
            using FileStream stream = new(plan.Path, mode, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough);
            policy.ValidatePath(plan.Path);
            bool truncate = plan.Operation is "file.create" or "file.replace" or "file.upsert";
            if (truncate)
            {
                stream.SetLength(0);
            }
            string append = "file.append";
            if (string.Equals(plan.Operation, append, StringComparison.Ordinal))
            {
                stream.Seek(0, SeekOrigin.End);
            }
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            return null;
        }
        catch (IOException error) when (FileOutputPolicy.IsLock(error))
        {
            return FileOutputCommit.Value(FileOutputPolicy.Locked(context, plan.Path, Contracts.Execution.EOutcome.Failed));
        }
        catch (IOException) when (existing is "skip" && File.Exists(plan.Path) && !Directory.Exists(plan.Path))
        {
            policy.ValidatePath(plan.Path);
            return FileOutputCommit.Value(FileOutputPolicy.Skipped(context.PeerId, plan.Path, "existing"));
        }
    }

    /// <summary>両パスの許可範囲と上書き指定を確認してcopyまたは移動します。</summary>
    /// <returns>スキップ値、通常確定はnullです。</returns>
    private static async Task<NodeValue?> RelocateAsync(FileOperationPlan plan, NodeExecutionContext context, FileAccessPolicy policy, CancellationToken cancellationToken = default)
    {
        NodeSettings settings = new(context.Settings);
        string destination = plan.Destination ?? throw new IOException("宛先が必要です。");
        bool directory = plan.Operation is "directory.copy" or "directory.move" or "directory.rename";
        bool copy = plan.Operation is "file.copy" or "directory.copy";
        if (directory)
        {
            if (!Directory.Exists(plan.Path))
            {
                throw new DirectoryNotFoundException("元ディレクトリがありません。");
            }
            FileTree.ValidateTree(plan.Path, policy);
            bool recursive = settings.ReadBoolean("recursive");
            if (copy && !recursive && Directory.EnumerateFileSystemEntries(plan.Path).Any())
            {
                throw new IOException("空でないdirectory copyにはrecursiveが必要です。");
            }
        }
        else
        {
            RequireFileOrMissing(plan.Path, false);
        }
        FileOutputPolicy.EnsureParent(policy, destination);
        policy.ValidatePath(destination);
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            if (Directory.Exists(destination) != directory)
            {
                throw new IOException("宛先のfile/directory種類が一致しません。");
            }
        }
        policy.ValidatePath(plan.Path, copy);
        policy.ValidatePath(destination);
        return await FileOutputCommit.RelocateAsync(plan, context, policy, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>勝手な祖先作成を避けるため親の存在を要求します。</summary>
    private static void RequireParent(string path)
    {
        if (!Directory.Exists(Path.GetDirectoryName(path)))
        {
            throw new DirectoryNotFoundException("親ディレクトリを先に作成してください。");
        }
    }

    /// <summary>新規作成の同名衝突を拒否します。</summary>
    private static void RequireAbsent(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new IOException("同名の実体が存在します。");
        }
    }

    /// <summary>種類違いと許可されない不存在を拒否します。</summary>
    private static void RequireFileOrMissing(string path, bool missingOk)
    {
        if (Directory.Exists(path) || (!missingOk && !File.Exists(path)))
        {
            throw new FileNotFoundException("対象ファイルがありません。");
        }
    }
}
