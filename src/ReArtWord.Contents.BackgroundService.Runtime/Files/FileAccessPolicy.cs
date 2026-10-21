using ReArtWord.Contents.BackgroundService.Contracts.Files;

namespace ReArtWord.Contents.BackgroundService.Runtime.Files;

/// <summary>CoreのOSアクセス方式と内部処理の限定rootを区別し、全祖先のlink境界を検証します。</summary>
public sealed class FileAccessPolicy
{
    private readonly string[] roots;
    private readonly StringComparison comparison;
    private readonly bool useOperatingSystemAccess;

    /// <summary>ファイル機能のOSアクセス方式、または内部処理の限定rootを固定します。</summary>
    public FileAccessPolicy(FileSystemOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        useOperatingSystemAccess = options.UseOperatingSystemAccess;
        comparison = StringComparison.Ordinal;
        if (OperatingSystem.IsWindows())
        {
            comparison = StringComparison.OrdinalIgnoreCase;
        }
        if (options.AllowedRoots.Any(root => !Path.IsPathFullyQualified(root)))
        {
            throw new ArgumentException("許可rootは絶対pathで指定してください。", nameof(options));
        }
        roots = options.AllowedRoots.Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))).ToArray();

    }

    /// <summary>絶対パスを正規化し、許可範囲とlink非通過を確認します。</summary>
    /// <returns>操作直前にも再検証できる絶対パスです。</returns>
    public string ResolvePath(string path, bool allowRoot = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new UnauthorizedAccessException("ファイルpathは絶対pathで指定してください。");
        }
        string resolved = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string? root = roots.FirstOrDefault(candidate => string.Equals(resolved, candidate, comparison) || resolved.StartsWith(candidate.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison));
        bool rootTarget = roots.Any(candidate => string.Equals(candidate, resolved, comparison));
        if (useOperatingSystemAccess)
        {
            root = Path.GetPathRoot(resolved);
            rootTarget = string.Equals(root, resolved, comparison);
        }
        if (root is null || (!allowRoot && rootTarget))
        {
            throw new UnauthorizedAccessException("許可root外またはroot自体の変更はできません。");
        }
        RejectLinks(resolved);
        return resolved;
    }

    /// <summary>直前に取得したパスの祖先変更も再検証します。</summary>
    public void ValidatePath(string path, bool allowRoot = false)
    {
        _ = ResolvePath(path, allowRoot);
    }

    /// <summary>存在する祖先のsymlink、junction、reparsepointを拒否します。</summary>
    private static void RejectLinks(string path)
    {
        string? current = path;
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new UnauthorizedAccessException("symlinkまたはreparsepointを通る操作は許可されません。");
                }
            }
            catch (FileNotFoundException)
            {
                // 新規作成先は存在する祖先まで辿って確認します。
            }
            catch (DirectoryNotFoundException)
            {
                // 新規作成先は存在する祖先まで辿って確認します。
            }
            current = Path.GetDirectoryName(current);
        }
    }
}
