using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Files;

namespace ReArtWord.Contents.BackgroundService.Nodes.FileSystem;

/// <summary>操作対象、変更範囲と競合範囲を開始前に固定します。</summary>
/// <param name="Operation">操作種類。</param>
/// <param name="Path">対象絶対パス。</param>
/// <param name="Destination">必要時の宛先。</param>
/// <param name="Scopes">読書き競合範囲。</param>
internal sealed record FileOperationPlan(string Operation, string Path, string? Destination, IReadOnlyList<FileAccessScope> Scopes)
{
    /// <summary>公開設定から全操作範囲を計画します。</summary>
    /// <returns>正規化済みの計画です。</returns>
    internal static FileOperationPlan Create(string operation, NodeSettings settings, FileAccessPolicy policy)
    {
        bool read = operation is "file.read" or "file.exists" or "directory.exists" or "directory.list";
        bool copy = operation is "file.copy" or "directory.copy";
        bool relocate = operation is "file.move" or "file.rename" or "directory.move" or "directory.rename";
        string path = policy.ResolvePath(settings.ReadText("path") ?? string.Empty, read || copy);
        string? destination = null;
        List<FileAccessScope> scopes = [new(path, !read && !copy)];
        string createDirectory = "directory.create";
        if (string.Equals(operation, createDirectory, StringComparison.Ordinal))
        {
            string first = path;
            string? parent = System.IO.Path.GetDirectoryName(first);
            while (parent is not null && !Directory.Exists(parent))
            {
                first = policy.ResolvePath(parent);
                if (File.Exists(first))
                {
                    throw new IOException("作成先の親階層にファイルがあります。");
                }
                parent = System.IO.Path.GetDirectoryName(first);
            }
            scopes[0] = new(first, true);
        }
        if (copy || relocate)
        {
            destination = policy.ResolvePath(settings.ReadText("destination") ?? string.Empty);
            StringComparison comparison = StringComparison.Ordinal;
            if (OperatingSystem.IsWindows())
            {
                comparison = StringComparison.OrdinalIgnoreCase;
            }
            if (string.Equals(path, destination, comparison)
                || destination.StartsWith(path.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar, comparison)
                || path.StartsWith(destination.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar, comparison))
            {
                throw new IOException("sourceとdestinationの同一または包含関係は使用できません。");
            }
            bool rename = operation is "file.rename" or "directory.rename";
            if (rename && !string.Equals(System.IO.Path.GetDirectoryName(path), System.IO.Path.GetDirectoryName(destination), comparison))
            {
                throw new IOException("renameでは同じ親ディレクトリ内の名前を指定してください。");
            }
            scopes.Add(new(destination, true));
        }
        return new(operation, path, destination, scopes);
    }
}
