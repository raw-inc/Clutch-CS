using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Client;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Local;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Runtime.Processes;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Client;

/// <summary>元ClientHostと同じ利用者で起動したhelperが、固定パスの入替と実起動確認を行います。</summary>
public static class ClientUpdateExecutor
{
    /// <summary>claim済み操作を再実行せず、自然終了後だけ実体を入れ替えます。</summary>
    /// <returns>実確認済みの成功・復元または結果不明です。</returns>
    public static async Task<ClientUpdateProgress> ExecuteAsync(ClientUpdateJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ClientUpdateFiles files = new(job);
        files.Validate(files.Operation);
        Directory.CreateDirectory(files.Operation);
        string claimPath = Path.Combine(files.Operation, "claimed");
        if (File.Exists(claimPath))
        {
            if (!string.Equals(await File.ReadAllTextAsync(claimPath, cancellationToken).ConfigureAwait(false), ContractJson.Hash(job), StringComparison.Ordinal))
            {
                throw new InvalidDataException("The claimed ClientHost operation was changed.");
            }
            return await ClientUpdateFiles.ReadAsync(job.StateDirectory, job.OperationId, cancellationToken).ConfigureAwait(false)
                ?? new("Unconfirmed", true, EOutcome.Unknown, null, DateTimeOffset.UtcNow, "The saved ClientHost operation was already claimed.");
        }
        files.Claim(ContractJson.Hash(job));
        bool stopped = false;
        try
        {
            ValidateRelease(job.Manifest);
            if (UpdateLayout.Contains(files.Installation, AppContext.BaseDirectory))
            {
                throw new InvalidDataException("ClientHost updater must run outside the installation.");
            }
            ReleaseManifest previous = await LocalUpdateExecutor.ReadManifestAsync(files.Installation, cancellationToken).ConfigureAwait(false);
            ValidateRelease(previous);
            bool service = await ClientUpdateStartup.ReadServiceAsync(job, cancellationToken).ConfigureAwait(false) is not null;
            if (OperatingSystem.IsWindows() && service)
            {
                WindowsInstallationSecurity.ProtectTree(files.Candidate, perUser: false);
            }
            await ReleaseVerifier.VerifyAsync(previous, files.Installation, cancellationToken).ConfigureAwait(false);
            await ReleaseVerifier.VerifyAsync(job.Manifest, files.Candidate, cancellationToken).ConfigureAwait(false);
            using Process? original = ProcessOs.Open(job.PreviousProcess);
            if (Directory.Exists(files.Backup) || original is null || original.HasExited)
            {
                return await files.SaveAsync("Failed", true, EOutcome.NotExecuted, "The original ClientHost or backup location changed.", cancellationToken).ConfigureAwait(false);
            }
            await files.SaveAsync("WaitingForExit", false, EOutcome.Unknown, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!await WaitForExitAsync(original, job.TimeoutMilliseconds, cancellationToken).ConfigureAwait(false))
            {
                return await files.SaveAsync("Failed", true, EOutcome.NotExecuted, "The original ClientHost did not exit; its files were not changed.", cancellationToken).ConfigureAwait(false);
            }
            stopped = true;
            if (OperatingSystem.IsWindows() && service)
            {
                WindowsInstallationSecurity.ProtectTree(files.Installation, perUser: false);
            }
            await files.SaveAsync("Installing", false, EOutcome.Unknown, cancellationToken: cancellationToken).ConfigureAwait(false);
            try
            {
                await UpdateDirectorySwitch.MoveAsync(files.Installation, files.Backup, files.Validate, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException) when (Directory.Exists(files.Installation) && !Directory.Exists(files.Backup))
            {
                return await RestoreStartAsync(job, previous, files, cancellationToken).ConfigureAwait(false);
            }
            try
            {
                await UpdateDirectorySwitch.MoveAsync(files.Candidate, files.Installation, files.Validate, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException) when (!Directory.Exists(files.Installation) && Directory.Exists(files.Backup))
            {
                await UpdateDirectorySwitch.MoveAsync(files.Backup, files.Installation, files.Validate, cancellationToken).ConfigureAwait(false);
                return await RestoreStartAsync(job, previous, files, cancellationToken).ConfigureAwait(false);
            }
            await files.SaveAsync("Starting", false, EOutcome.Unknown, cancellationToken: cancellationToken).ConfigureAwait(false);
            using Process? process = await ClientUpdateStartup.StartAsync(job, job.Manifest, cancellationToken).ConfigureAwait(false);
            if (process is not null && await WaitReadyAsync(job, job.Manifest, ProcessOs.Identity(process), cancellationToken).ConfigureAwait(false))
            {
                return await files.SaveAsync("Completed", true, EOutcome.Succeeded, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            // 自分が起動した新版の停止だけを確認し、他プロセスや不明な状態を強制的に入れ替えません。
            if (process is not null && !await ClientUpdateStartup.StopAsync(job, process, cancellationToken).ConfigureAwait(false))
            {
                return await files.SaveAsync("Unconfirmed", true, EOutcome.Unknown, "The new ClientHost could not be stopped for restoration.", cancellationToken).ConfigureAwait(false);
            }
            await files.SaveAsync("Restoring", false, EOutcome.Unknown, cancellationToken: cancellationToken).ConfigureAwait(false);
            await UpdateDirectorySwitch.MoveAsync(files.Installation, files.Candidate, files.Validate, cancellationToken).ConfigureAwait(false);
            await UpdateDirectorySwitch.MoveAsync(files.Backup, files.Installation, files.Validate, cancellationToken).ConfigureAwait(false);
            return await RestoreStartAsync(job, previous, files, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException
            or System.Text.Json.JsonException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            EOutcome outcome = EOutcome.NotExecuted;
            if (stopped)
            {
                outcome = EOutcome.Unknown;
            }
            return await files.SaveAsync("Unconfirmed", true, outcome,
                "ClientHost update stopped: " + exception.GetType().Name + ". Inspect the saved operation before continuing.", CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>ClientHost配布と現在のplatformだけを受け付けます。</summary>
    public static void ValidateRelease(ReleaseManifest manifest)
    {
        ReleaseVerifier.Validate(manifest);
        string entry = "Clutch-CS.ClientHost";
        if (OperatingSystem.IsWindows())
        {
            entry += ".exe";
        }
        if (!string.Equals(manifest.EntryPoint, entry, StringComparison.Ordinal)
            || !string.Equals(manifest.Platform, RuntimeInformation.RuntimeIdentifier, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(manifest.ExpectedBuildVersion)
            || manifest.Files.Any(file => string.Equals(file.Path, LocalUpdateExecutor.ManifestFileName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("The release is not a versioned ClientHost for this platform.");
        }
    }

    /// <summary>元ClientHostには停止要求も強制終了も追加せず、親が保存後に自然終了するのを待ちます。</summary>
    /// <returns>固定した実体が終了したことの確認です。</returns>
    internal static async Task<bool> WaitForExitAsync(Process process, int milliseconds, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(milliseconds);
        try
        {
            // WaitingForExitを公開する前に確保したハンドルを使います。親は公開後すぐに終了し得ます。
            await ProcessCompletion.WaitAsync(process, deadline.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }

    /// <summary>HTTP応答が起動した実体と期待するassembly版に一致するまで有限時間待ちます。</summary>
    /// <returns>別アプリや旧プロセスの応答ではない健康確認です。</returns>
    private static async Task<bool> WaitReadyAsync(ClientUpdateJob job, ReleaseManifest manifest, ProcessIdentity process, CancellationToken cancellationToken = default)
    {
        using HttpClient client = new() { BaseAddress = job.Listen, Timeout = TimeSpan.FromSeconds(2) };
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(job.TimeoutMilliseconds);
        try
        {
            while (ProcessOs.IsRunning(process) == true)
            {
                try
                {
                    ClientUpdateHealth? health = await client.GetFromJsonAsync<ClientUpdateHealth>(new Uri("client/health", UriKind.Relative), ContractJson.Options, deadline.Token).ConfigureAwait(false);
                    if (health?.Process == process && string.Equals(health.BuildVersion, manifest.ExpectedBuildVersion, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
                catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException or OperationCanceledException)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                }
                await Task.Delay(200, deadline.Token).ConfigureAwait(false);
            }
            return false;
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }

    /// <summary>確認可能な失敗だけ旧版へ戻し、その実起動まで確認します。</summary>
    /// <returns>旧版復帰済みの失敗、または復帰未確認です。</returns>
    private static async Task<ClientUpdateProgress> RestoreStartAsync(ClientUpdateJob job, ReleaseManifest previous, ClientUpdateFiles files, CancellationToken cancellationToken = default)
    {
        await ReleaseVerifier.VerifyAsync(previous, files.Installation, cancellationToken).ConfigureAwait(false);
        using Process? process = await ClientUpdateStartup.StartAsync(job, previous, cancellationToken).ConfigureAwait(false);
        if (process is not null && await WaitReadyAsync(job, previous, ProcessOs.Identity(process), cancellationToken).ConfigureAwait(false))
        {
            return await files.SaveAsync("RolledBack", true, EOutcome.Failed, "ClientHost startup failed; the previous version was restored and verified.", cancellationToken).ConfigureAwait(false);
        }
        return await files.SaveAsync("Unconfirmed", true, EOutcome.Unknown, "ClientHost restoration could not be confirmed.", cancellationToken).ConfigureAwait(false);
    }
}
