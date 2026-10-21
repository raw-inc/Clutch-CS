using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;

namespace ReArtWord.Contents.BackgroundService.Nodes.FileSystem.Transfers;

/// <summary>件数を制限したページのsource一覧から転送するtreeを固定します。</summary>
internal static class FileTransferPlan
{
    /// <summary>source OSのパス区切りに依存せず相対マニフェストを組み立てます。</summary>
    /// <returns>内容とsource パスの一覧です。</returns>
    internal static async Task<IReadOnlyList<FileTransferItem>> CollectAsync(FileTransferSource source, string path, int maximum, CancellationToken cancellationToken = default)
    {
        FileEntryInfo root = await source.StatAsync(path, cancellationToken).ConfigureAwait(false);
        List<FileTransferItem> items = [new(new(string.Empty, root.IsDirectory, root.Length, root.Sha256), root.Path)];
        Queue<(string Path, string Relative)> directories = new();
        if (root.IsDirectory)
        {
            directories.Enqueue((root.Path, string.Empty));
        }
        while (directories.TryDequeue(out (string Path, string Relative) directory))
        {
            string? continuation = null;
            do
            {
                FileListPage page = await source.ListAsync(directory.Path, continuation, cancellationToken).ConfigureAwait(false);
                foreach (FileEntryInfo entry in page.Entries)
                {
                    if (maximum <= items.Count)
                    {
                        throw new IOException("転送treeのmaxEntriesを超えました。");
                    }
                    string name = entry.Path[(entry.Path.LastIndexOfAny(['/', '\\']) + 1)..];
                    string relative = name;
                    if (!string.IsNullOrEmpty(directory.Relative))
                    {
                        relative = directory.Relative + "/" + name;
                    }
                    items.Add(new(new(relative, entry.IsDirectory, entry.Length, entry.Sha256), entry.Path));
                    if (entry.IsDirectory)
                    {
                        directories.Enqueue((entry.Path, relative));
                    }
                }
                if (page.Continuation is not null && string.Equals(page.Continuation, continuation, StringComparison.Ordinal))
                {
                    throw new IOException("source一覧の継続位置が進みません。");
                }
                continuation = page.Continuation;
            }
            while (continuation is not null);
        }
        return items;
    }

    /// <summary>全ファイルを有限分割データで渡し、source再読取りで内容版を再確認します。</summary>
    /// <returns>転送片の保存完了です。</returns>
    internal static async Task CopyAsync(FileTransferSource source, FileTransferService destination, FileStageToken stage, IReadOnlyList<FileTransferItem> items, CancellationToken cancellationToken = default)
    {
        foreach (FileTransferItem item in items)
        {
            FileManifestEntry entry = item.Entry;
            if (entry.IsDirectory)
            {
                await destination.StageDirectoryAsync(new(stage, entry.RelativePath), cancellationToken).ConfigureAwait(false);
                continue;
            }
            long offset = 0;
            do
            {
                FileChunk chunk = await source.ReadAsync(new(item.SourcePath, offset, 32768, entry.Length, entry.Sha256), cancellationToken).ConfigureAwait(false);
                if (chunk.Offset != offset || (chunk.Data.Length == 0 && offset < entry.Length))
                {
                    throw new IOException("source chunkの進行が不正です。");
                }
                await destination.WriteChunkAsync(new(stage, entry.RelativePath, offset, chunk.Data, chunk.Sha256, entry.Length, entry.Sha256), cancellationToken).ConfigureAwait(false);
                offset += chunk.Data.Length;
            }
            while (offset < entry.Length);
        }
    }
}
