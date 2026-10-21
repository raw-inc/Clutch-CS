using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Local;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;
using System.Diagnostics;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Installation;

/// <summary>削除対象の外にhelperと耐久結果を配置します。</summary>
public static class SetupWorkspace
{
    /// <summary>更新後のプログラムに同梱されたUpdaterを優先し、古い保守実行物への依存を避けます。</summary>
    /// <returns>独立した保守実行物です。</returns>
    public static async Task<string> PrepareBundledAsync(SetupJob job, string programRoot, string fallbackRoot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        string manifest = Path.Combine(programRoot, "updater", LocalUpdateExecutor.ManifestFileName);
        if (!File.Exists(manifest))
        {
            return await PrepareAsync(job, fallbackRoot, cancellationToken).ConfigureAwait(false);
        }
        string workspace = InstallationFiles.Root(DirectoryFor(job.OperationId));
        if (Directory.Exists(workspace))
        {
            throw new IOException("This operation already exists. Inspect its result instead of restarting it.");
        }
        Directory.CreateDirectory(workspace);
        string executable = await BundledUpdater.ResolveAsync(programRoot, Path.Combine(workspace, "helper"), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The bundled updater is missing.");
        await InstallationFiles.WriteAsync(Path.Combine(workspace, "job.json"), job, cancellationToken).ConfigureAwait(false);
        return executable;
    }

    /// <summary>同一利用者の操作記録保管場所です。</summary>
    /// <returns>operation専用絶対パスです。</returns>
    public static string DirectoryFor(Guid operationId) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReArtWord", "SetupOperations", operationId.ToString("N"));

    /// <summary>検証したUpdaterを実行対象の外へ複製します。</summary>
    /// <returns>helperの絶対パスです。</returns>
    public static Task<string> PrepareAsync(SetupJob job, string updaterRoot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        return PrepareAsync(job.OperationId, job, updaterRoot, cancellationToken);
    }

    /// <summary>操作の種類に依存せず、固定入力とhelperを独立した領域へ保存します。</summary>
    /// <returns>検証済みhelperの絶対pathです。</returns>
    public static async Task<string> PrepareAsync<T>(Guid operationId, T job, string updaterRoot, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(operationId, Guid.Empty);
        string workspace = InstallationFiles.Root(DirectoryFor(operationId));
        if (Directory.Exists(workspace))
        {
            throw new IOException("This operation already exists. Inspect its result instead of restarting it.");
        }
        Directory.CreateDirectory(workspace);
        ReleaseManifest manifest = await LocalUpdateExecutor.ReadManifestAsync(updaterRoot, cancellationToken).ConfigureAwait(false);
        string helper = Path.Combine(workspace, "helper");
        await ReleaseVerifier.CopyAsync(manifest, updaterRoot, helper, cancellationToken).ConfigureAwait(false);
        await InstallationFiles.WriteAsync(Path.Combine(workspace, "job.json"), job, cancellationToken).ConfigureAwait(false);
        return Path.Combine(helper, manifest.EntryPoint);
    }

    /// <summary>確認済みjobを一度だけ別processへ渡します。</summary>
    /// <returns>開始したprocessです。呼出側がhandleを所有します。</returns>
    public static Process Launch(string helper, string jobPath, bool elevate, string option = "--setup-job")
    {
        ProcessStartInfo start = new(helper) { UseShellExecute = elevate, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(helper)! };
        if (elevate)
        {
            start.Verb = "runas";
        }
        start.ArgumentList.Add(option);
        start.ArgumentList.Add(jobPath);
        return Process.Start(start) ?? throw new IOException("The setup helper could not start.");
    }
}
