using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Browsing;

namespace ReArtWord.Contents.BackgroundService.Runtime.Files.Browsing;

/// <summary>ファイル操作と同じ許可ルート・リンク境界で、内容を読まず候補を列挙します。</summary>
public sealed class FileBrowser(FileSystemOptions options)
{
    private readonly FileAccessPolicy policy = new(options);

    /// <summary>許可範囲を越えず、返す件数と走査量を制限します。</summary>
    /// <returns>成功ページ、または利用者へ表示できる理由です。</returns>
    public FileBrowseResult Browse(FileBrowseRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfNegative(request.Offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.Offset, 100000);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[] roots = options.AllowedRoots.ToArray();
            FileAccessPolicy browsing = policy;
            if (request.FromDrives || options.UseOperatingSystemAccess)
            {
                roots = DriveInfo.GetDrives().Select(drive => drive.RootDirectory.FullName).ToArray();
                if (!options.UseOperatingSystemAccess)
                {
                    browsing = new(new FileSystemOptions(roots));
                }
            }
            if (string.IsNullOrWhiteSpace(request.Path))
            {
                return new(null, null, roots.Select(root => new FileBrowseEntry(root, root, true)).ToArray());
            }
            string path = browsing.ResolvePath(request.Path, true);
            string? parent = System.IO.Path.GetDirectoryName(path);
            try
            {
                if (parent is not null)
                {
                    parent = browsing.ResolvePath(parent, true);
                }
            }
            catch (UnauthorizedAccessException)
            {
                parent = null;
            }
            List<FileBrowseEntry> entries = [];
            int index = 0;
            int bytes = 0;
            foreach (string entry in Directory.EnumerateFileSystemEntries(path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (index++ < request.Offset)
                {
                    continue;
                }
                if (128 <= entries.Count || 32768 <= bytes || 512 < index - request.Offset)
                {
                    if (100000 < index - 1)
                    {
                        return new(path, parent, entries.AsReadOnly(), Problem: "This folder is too large to browse further. Type the path directly.");
                    }
                    return new(path, parent, entries.AsReadOnly(), index - 1);
                }
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }
                string resolved = browsing.ResolvePath(entry, true);
                entries.Add(new(System.IO.Path.GetFileName(resolved), resolved, (attributes & FileAttributes.Directory) != 0));
                bytes += resolved.Length * 2;
            }
            return new(path, parent, entries.AsReadOnly());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            string problem = "The directory is unavailable or outside this peer's allowed file roots.";
            if (request.FromDrives || options.UseOperatingSystemAccess)
            {
                problem = "This directory is unavailable, is a link, or cannot be read by this peer's service account.";
            }
            return new(request.Path, null, [], Problem: problem);
        }
    }
}
