using System.Diagnostics;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Client;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Client;

/// <summary>ClientHostの更新後も、導入時に選択した起動方式を維持します。</summary>
internal static class ClientUpdateStartup
{
    private static TimeSpan FailedProcessStopTimeout => TimeSpan.FromSeconds(1);

    /// <summary>通常の導入は直接起動し、サービス導入は同じサービスを開始します。</summary>
    /// <returns>新しく起動した実体、または停止を確認した起動失敗です。</returns>
    internal static async Task<ProcessIdentity?> StartAsync(ClientUpdateJob job, ReleaseManifest manifest, CancellationToken cancellationToken = default)
    {
        WebClientInstallation? service = await ReadServiceAsync(job, cancellationToken).ConfigureAwait(false);
        if (service is null)
        {
            try
            {
                return ProcessOs.Start(new("client-update", EProcessMode.Managed, Path.Combine(job.InstallationRoot, manifest.EntryPoint),
                    ["--listen", job.Listen.AbsoluteUri, "--state", job.StateDirectory], job.InstallationRoot));
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return null;
            }
        }
        StartupRegistration registration = StartupRegistration.ForClient(service);
        if (OperatingSystem.IsWindows())
        {
            await new WindowsStartup().StartAsync(registration, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await UnixInstallation.StartAsync(registration, cancellationToken).ConfigureAwait(false);
        }
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMilliseconds(job.TimeoutMilliseconds);
        string executable = Path.Combine(job.InstallationRoot, manifest.EntryPoint);
        while (DateTimeOffset.UtcNow < deadline)
        {
            foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
            {
                using (process)
                {
                    if (string.Equals(ProcessExecutable.GetPath(process), executable, StringComparison.OrdinalIgnoreCase))
                    {
                        ProcessIdentity identity = ProcessOs.Identity(process);
                        if (identity != job.PreviousProcess)
                        {
                            return identity;
                        }
                    }
                }
            }
            await Task.Delay(InstallationDefaults.PollInterval, cancellationToken).ConfigureAwait(false);
        }
        await StopServiceAsync(registration, cancellationToken).ConfigureAwait(false);
        return null;
    }

    /// <summary>切り戻し前に、自分が開始した実体の停止を確認します。</summary>
    /// <returns>停止を確認できたかです。</returns>
    internal static async Task<bool> StopAsync(ClientUpdateJob job, ProcessIdentity process, CancellationToken cancellationToken = default)
    {
        WebClientInstallation? service = await ReadServiceAsync(job, cancellationToken).ConfigureAwait(false);
        if (service is not null)
        {
            await StopServiceAsync(StartupRegistration.ForClient(service), cancellationToken).ConfigureAwait(false);
            return ProcessOs.IsRunning(process) == false;
        }
        return await ProcessOs.StopAsync(process, EProcessStopPolicy.Force, (int)FailedProcessStopTimeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false) == EOutcome.Succeeded;
    }

    /// <summary>導入記録が対象プログラムと保存先に一致する場合だけサービス操作を許可します。</summary>
    /// <returns>対応するサービス導入、通常起動ならnullです。</returns>
    internal static async Task<WebClientInstallation?> ReadServiceAsync(ClientUpdateJob job, CancellationToken cancellationToken = default)
    {
        string root = Path.GetDirectoryName(InstallationFiles.Root(job.InstallationRoot))!;
        if (!File.Exists(Path.Combine(root, WebClientCatalog.RecordName)))
        {
            return null;
        }
        WebClientInstallation client = await WebClientCatalog.ReadAsync(root, false, cancellationToken).ConfigureAwait(false);
        if (!client.RunAsService)
        {
            return null;
        }
        if (!string.Equals(WebClientCatalog.ProgramRoot(client), InstallationFiles.Root(job.InstallationRoot), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(WebClientStartup.StateDirectory(client), Path.GetFullPath(job.StateDirectory), StringComparison.OrdinalIgnoreCase) || client.Port != job.Listen.Port)
        {
            throw new InvalidDataException("The ClientHost service does not match the update target.");
        }
        return client;
    }

    /// <summary>選択したOSのサービス停止を待ちます。</summary>
    /// <returns>停止確認です。</returns>
    private static Task StopServiceAsync(StartupRegistration registration, CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsStartup().StopAsync(registration, cancellationToken);
        }
        return UnixInstallation.StopAsync(registration, cancellationToken);
    }
}
