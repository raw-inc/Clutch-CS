using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Local;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Runtime.Files;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Updates;

/// <summary>アプリに同梱されたUpdaterを、入替対象外の検証済み実行imageへ組み立てます。</summary>
public static class BundledUpdater
{
    /// <summary>同梱版を優先し、同梱されていない従来リリースだけ明示設定を使用します。</summary>
    /// <returns>停止するCoreから独立したUpdaterの絶対パスです。</returns>
    public static async Task<string?> ResolveAsync(UpdatesOptions options, Guid operationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        UpdateLayout layout = new(options, operationId);
        return await ResolveAsync(layout.InstallationRoot, Path.Combine(layout.OperationRoot, "helper"), cancellationToken).ConfigureAwait(false)
            ?? options.UpdaterExecutablePath;
    }

    /// <summary>同じmanifest照合規則でCoreとClientHostの同梱helperを組み立てます。</summary>
    /// <returns>独立した実行物、同梱なしならnullです。</returns>
    public static async Task<string?> ResolveAsync(string installationRoot, string destination, CancellationToken cancellationToken = default)
    {
        if (UpdateLayout.Contains(installationRoot, destination) || UpdateLayout.Contains(destination, installationRoot))
        {
            throw new InvalidDataException("Updater must be outside the replaced installation.");
        }
        FileAccessPolicy policy = new(new FileSystemOptions([installationRoot, destination]));
        policy.ValidatePath(installationRoot, true);
        policy.ValidatePath(destination, true);
        ReleaseManifest core = await LocalUpdateExecutor.ReadManifestAsync(installationRoot, cancellationToken).ConfigureAwait(false);
        string manifestPath = "updater/release-manifest.json";
        ReleaseFile? declared = core.Files.SingleOrDefault(file => string.Equals(file.Path, manifestPath, StringComparison.Ordinal));
        if (declared is null)
        {
            return null;
        }
        await ReleaseVerifier.VerifyAsync(core with { EntryPoint = manifestPath, Files = [declared] }, installationRoot, cancellationToken).ConfigureAwait(false);
        ReleaseManifest updater = await LocalUpdateExecutor.ReadManifestAsync(Path.Combine(installationRoot, "updater"), cancellationToken).ConfigureAwait(false);
        ReleaseVerifier.Validate(updater);
        string executable = "ReArtWord.Contents.BackgroundService.Updater";
        if (OperatingSystem.IsWindows())
        {
            executable += ".exe";
        }
        if (!string.Equals(updater.EntryPoint, executable, StringComparison.Ordinal) || !string.Equals(updater.Platform, core.Platform, StringComparison.Ordinal)
            || !string.Equals(updater.ExpectedBuildVersion, core.ExpectedBuildVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException("同梱UpdaterのentryPoint、platformまたはbuildが一致しません。");
        }
        if (!Directory.Exists(destination))
        {
            Directory.CreateDirectory(destination);
            Dictionary<string, ReleaseFile> available = core.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
            foreach (ReleaseFile file in updater.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string sourcePath = "updater/" + file.Path;
                if (!available.TryGetValue(sourcePath, out ReleaseFile? source))
                {
                    sourcePath = file.Path;
                    source = available.GetValueOrDefault(sourcePath);
                }
                if (source is null || source.Size != file.Size || !string.Equals(source.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("同梱Updaterの依存fileをCore manifestで確認できません。");
                }
                string input = Path.Combine(installationRoot, sourcePath.Replace('/', Path.DirectorySeparatorChar));
                string output = Path.Combine(destination, file.Path.Replace('/', Path.DirectorySeparatorChar));
                policy.ValidatePath(input);
                policy.ValidatePath(output);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                policy.ValidatePath(output);
                File.Copy(input, output, overwrite: false);
                if (file.Executable && (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
                {
                    File.SetUnixFileMode(output, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }
            }
        }
        await ReleaseVerifier.VerifyAsync(updater, destination, cancellationToken).ConfigureAwait(false);
        return Path.Combine(destination, updater.EntryPoint);
    }
}
