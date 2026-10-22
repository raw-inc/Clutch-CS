using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Runtime.Files;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Installation;

/// <summary>配置の所有記録と検証済みディレクトリを扱います。</summary>
public static class InstallationFiles
{
    /// <summary>所有記録の固定名です。</summary>
    public static string RecordName => "installation.json";

    /// <summary>volume root、相対path、reparse pointを拒否します。</summary>
    /// <returns>検証済みの絶対rootです。</returns>
    public static string Root(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException("Select an absolute folder path.");
        }
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (string.Equals(root, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(root)!), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A drive root cannot be used as an installation folder.");
        }
        new FileAccessPolicy(new FileSystemOptions([root])).ValidatePath(root, true);
        return root;
    }

    /// <summary>記録のrootと実際の保管先を一致させます。</summary>
    /// <returns>このrootの所有記録です。</returns>
    public static async Task<InstallationRecord> ReadAsync(string root, CancellationToken cancellationToken = default)
    {
        string canonical = Root(root);
        InstallationRecord record = JsonSerializer.Deserialize<InstallationRecord>(await File.ReadAllTextAsync(Path.Combine(canonical, RecordName), cancellationToken).ConfigureAwait(false), ContractJson.Options)
            ?? throw new InvalidDataException("The installation record is missing.");
        if (record.InstallationId == Guid.Empty || !string.Equals(canonical, Root(record.Settings.Root), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The installation record does not belong to this folder.");
        }
        string serviceName = "Clutch-CS-" + record.InstallationId.ToString("N");
        if (!string.Equals(record.ServiceName, serviceName, StringComparison.Ordinal) || !ReleaseVerifier.IsRelativePath(record.CoreEntryPoint) || !ReleaseVerifier.IsRelativePath(record.UpdaterEntryPoint) || !ReleaseVerifier.IsRelativePath(record.ClientEntryPoint))
        {
            throw new InvalidDataException("The installation's service or executable ownership is invalid.");
        }
        return record;
    }

    /// <summary>同じdirectory内の原子的置換で結果を保存します。</summary>
    /// <returns>保存完了です。</returns>
    public static Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken = default)
        => WriteTextAsync(path, JsonSerializer.Serialize(value, ContractJson.Options), cancellationToken);

    /// <summary>停止要求などの本文を、書込み完了後に同じディレクトリ内で公開します。</summary>
    /// <returns>本文の原子的置換の完了です。</returns>
    public static async Task WriteTextAsync(string path, string value, CancellationToken cancellationToken = default)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temporary, value, cancellationToken).ConfigureAwait(false);
        if (File.Exists(path))
        {
            // Windowsでも共有削除を許可した読取りを維持できるよう、既存ファイルは置換します。
            File.Replace(temporary, path, null);
        }
        else
        {
            File.Move(temporary, path);
        }
    }

    /// <summary>既存の所有記録を上書きせず初回だけ保存します。</summary>
    /// <returns>所有権確保の完了です。</returns>
    public static async Task CreateRecordAsync(InstallationRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        using FileStream file = new(Path.Combine(Root(record.Settings.Root), RecordName), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(file, record, ContractJson.Options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>削除直前に全子孫のreparse pointを確認します。</summary>
    public static void DeleteDirectory(string root, string child)
    {
        string canonical = Root(root);
        FileAccessPolicy policy = new(new FileSystemOptions([canonical]));
        string path = policy.ResolvePath(Path.Combine(canonical, child), true);
        if (!Directory.Exists(path))
        {
            return;
        }
        ValidateTree(path, policy);
        Directory.Delete(path, true);
    }

    /// <summary>停止直後のWindowsファイルロックを有限時間待ち、所有範囲内の一ファイルだけを削除します。</summary>
    /// <returns>ファイルの削除確認です。</returns>
    public static async Task DeleteFileAsync(string root, string relativePath, CancellationToken cancellationToken = default)
    {
        FileAccessPolicy policy = new(new FileSystemOptions([Root(root)]));
        System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (elapsed.Elapsed < InstallationDefaults.StopTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = policy.ResolvePath(Path.Combine(root, relativePath), true);
            try
            {
                File.Delete(path);
                return;
            }
            catch (Exception exception) when (OperatingSystem.IsWindows() && elapsed.Elapsed < InstallationDefaults.StopTimeout
                && exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(InstallationDefaults.PollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(policy.ResolvePath(Path.Combine(root, relativePath), true));
    }

    /// <summary>リンク先へ降りる前に検証します。</summary>
    private static void ValidateTree(string path, FileAccessPolicy policy)
    {
        policy.ValidatePath(path, true);
        foreach (string entry in Directory.EnumerateFileSystemEntries(path))
        {
            policy.ValidatePath(entry, Directory.Exists(entry));
            if (Directory.Exists(entry))
            {
                ValidateTree(entry, policy);
            }
        }
    }
}
