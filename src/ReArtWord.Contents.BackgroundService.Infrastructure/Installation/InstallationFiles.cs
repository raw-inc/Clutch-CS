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

    /// <summary>所有範囲を毎回確認し、停止後の一時的なファイル占有だけを有限回待ちます。</summary>
    /// <returns>既存の移動先を上書きしないディレクトリ移動です。</returns>
    internal static Task MoveDirectoryAsync(string root, string source, string destination, CancellationToken cancellationToken = default)
    {
        FileAccessPolicy policy = new(new FileSystemOptions([Root(root)]));

        // Windowsでは配下ファイルの占有もアクセス拒否になります。通常更新と同じ未適用確認を使います。
        return Updates.Local.UpdateDirectorySwitch.MoveAsync(policy.ResolvePath(source), policy.ResolvePath(destination),
            path => policy.ValidatePath(path), cancellationToken);
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

    /// <summary>削除途中の共有違反を待ち、残った対象を再検証して削除します。</summary>
    /// <returns>所有範囲内のディレクトリ削除です。</returns>
    public static Task DeleteDirectoryAsync(string root, string child, CancellationToken cancellationToken = default)
    {
        FileAccessPolicy policy = new(new FileSystemOptions([Root(root)]));
        string path = policy.ResolvePath(Path.Combine(root, child));
        return CompleteFileOperationAsync(path, () =>
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }, () =>
        {
            policy.ValidatePath(path);
            if (Directory.Exists(path))
            {
                ValidateTree(path, policy);
            }
        }, cancellationToken);
    }

    /// <summary>停止直後のWindowsファイルロックを有限時間待ち、所有範囲内の一ファイルだけを削除します。</summary>
    /// <returns>ファイルの削除確認です。</returns>
    public static Task DeleteFileAsync(string root, string relativePath, CancellationToken cancellationToken = default)
    {
        FileAccessPolicy policy = new(new FileSystemOptions([Root(root)]));
        string path = policy.ResolvePath(Path.Combine(root, relativePath));
        return CompleteFileOperationAsync(path, () => File.Delete(path), () => policy.ValidatePath(path), cancellationToken);
    }

    /// <summary>削除済みの導入記録への切替も、読み取り側の一時占有を待って確定します。</summary>
    /// <returns>所有範囲内での記録の移動です。</returns>
    internal static Task MoveFileAsync(string root, string sourceName, string destinationName, CancellationToken cancellationToken = default)
    {
        FileAccessPolicy policy = new(new FileSystemOptions([Root(root)]));
        string source = policy.ResolvePath(Path.Combine(root, sourceName));
        string destination = policy.ResolvePath(Path.Combine(root, destinationName));
        return CompleteFileOperationAsync(source, () => File.Move(source, destination, overwrite: true), () =>
        {
            policy.ValidatePath(source);
            policy.ValidatePath(destination);
        }, cancellationToken);
    }

    /// <summary>Windowsの占有エラーだけを待ち、最後の失敗には操作対象とOSのエラー番号を残します。</summary>
    /// <returns>成功した一回の操作、または保持した最終エラーです。</returns>
    private static async Task CompleteFileOperationAsync(string path, Action operation, Action validate, CancellationToken cancellationToken = default)
    {
        System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
        Exception? failure = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 境界・リンクの検証失敗は、OSの一時占有として再試行しません。
            validate();
            try
            {
                operation();
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failure = exception;
            }
            if (!OperatingSystem.IsWindows() || (failure.HResult & 0xffff) is not (5 or 32 or 33) || InstallationDefaults.StopTimeout <= elapsed.Elapsed)
            {
                break;
            }
            await Task.Delay(InstallationDefaults.PollInterval, cancellationToken).ConfigureAwait(false);
        }
        while (elapsed.Elapsed < InstallationDefaults.StopTimeout);
        cancellationToken.ThrowIfCancellationRequested();
        string code = failure!.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
        throw new IOException("The file operation could not finish at '" + path + "' (0x" + code + "). " + failure.Message, failure);
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
