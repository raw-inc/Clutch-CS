using System.Security.Cryptography;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Runtime.Files;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;

/// <summary>相対パスと全実ファイルを検証してリリースの破損・越境を拒否します。</summary>
public static class ReleaseVerifier
{
    /// <summary>マニフェストの構造とportable相対パスを検証します。</summary>
    public static void Validate(ReleaseManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifest.ReleaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifest.Platform);
        ArgumentNullException.ThrowIfNull(manifest.Files);
        ArgumentNullException.ThrowIfNull(manifest.CompatibleReleaseIds);
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        foreach (ReleaseFile file in manifest.Files)
        {
            ArgumentNullException.ThrowIfNull(file);
            bool validHash = file.Sha256 is not null && file.Sha256.Length == 64 && file.Sha256.All(Uri.IsHexDigit);
            if (!IsRelativePath(file.Path) || !paths.Add(file.Path) || file.Size < 0 || !validHash)
            {
                throw new InvalidDataException("releaseのpath、サイズまたはchecksumが不正です。");
            }
        }
        bool entryExists = manifest.Files.Any(file => string.Equals(file.Path, manifest.EntryPoint, StringComparison.Ordinal));
        if (!entryExists || !IsRelativePath(manifest.EntryPoint) || manifest.CompatibleReleaseIds.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidDataException("releaseのentryPointまたは互換性一覧が不正です。");
        }
    }

    /// <summary>全declared ファイルの実サイズとSHA256を取得して比較します。</summary>
    /// <returns>全内容の検証の完了。</returns>
    public static async Task VerifyAsync(ReleaseManifest manifest, string payloadRoot, CancellationToken cancellationToken = default)
    {
        Validate(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadRoot);
        FileAccessPolicy policy = new(new FileSystemOptions([payloadRoot]));
        policy.ValidatePath(payloadRoot, true);
        foreach (ReleaseFile file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = policy.ResolvePath(Path.Combine(payloadRoot, file.Path.Replace('/', Path.DirectorySeparatorChar)));
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            if (stream.Length != file.Size)
            {
                throw new InvalidDataException("releaseファイルのサイズが一致しません。");
            }
            byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(Convert.ToHexString(hash), file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("releaseファイルのchecksumが一致しません。");
            }
        }
    }

    /// <summary>マニフェストに列挙した検証可能な内容だけを新規候補へコピーします。</summary>
    /// <returns>候補ディレクトリの全内容検証の完了。</returns>
    public static async Task CopyAsync(ReleaseManifest manifest, string sourceRoot, string destinationRoot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        await VerifyAsync(manifest, sourceRoot, cancellationToken).ConfigureAwait(false);
        FileAccessPolicy sourcePolicy = new(new FileSystemOptions([sourceRoot]));
        FileAccessPolicy destinationPolicy = new(new FileSystemOptions([destinationRoot]));
        destinationPolicy.ValidatePath(destinationRoot, true);
        if (Directory.Exists(destinationRoot))
        {
            throw new IOException("release候補の出力先が既に存在します。");
        }
        Directory.CreateDirectory(destinationRoot);
        foreach (ReleaseFile file in manifest.Files)
        {
            string source = sourcePolicy.ResolvePath(Path.Combine(sourceRoot, file.Path.Replace('/', Path.DirectorySeparatorChar)));
            string destination = destinationPolicy.ResolvePath(Path.Combine(destinationRoot, file.Path.Replace('/', Path.DirectorySeparatorChar)));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            destinationPolicy.ValidatePath(destination);
            using FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (file.Executable && (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
            {
                File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }
        await VerifyAsync(manifest, destinationRoot, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>全OSで越境やdevice名解釈を起こさない相対パスだけを許可します。</summary>
    /// <returns>portableな通常ファイルパスであるかです。</returns>
    public static bool IsRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        string forbidden = "\\:<>\"|?*";
        foreach (string segment in path.Split('/'))
        {
            bool invalid = segment.Length == 0 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') || segment.Any(character => char.IsControl(character) || forbidden.Contains(character, StringComparison.Ordinal));
            string device = segment.Split('.')[0].ToUpperInvariant();
            bool reserved = device is "CON" or "PRN" or "AUX" or "NUL" || (device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal)) && char.IsAsciiDigit(device[3]));
            if (invalid || reserved)
            {
                return false;
            }
        }
        return true;
    }
}
