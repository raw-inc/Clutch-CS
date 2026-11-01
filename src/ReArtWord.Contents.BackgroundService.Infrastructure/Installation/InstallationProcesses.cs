using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Installation;

/// <summary>入替対象の配置から起動したclientだけを停止します。</summary>
internal static class InstallationProcesses
{
    /// <summary>別配置や同名の別実行物を停止せず、対象PIDの終了を確認します。</summary>
    /// <returns>対象clientの停止完了です。</returns>
    internal static async Task StopClientAsync(InstallationRecord record, CancellationToken cancellationToken = default)
    {
        string clientRecord = Path.Combine(record.Settings.Root, "client", WebClientCatalog.RecordName);
        if (record.Settings.WebClient is not null || File.Exists(clientRecord))
        {
            return;
        }
        string executable = Path.Combine(record.Settings.Root, "client", record.ClientEntryPoint);
        await StopExecutableAsync(executable, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>確認済み実行パスのプロセスだけを停止します。</summary>
    /// <returns>全対象の停止確認です。</returns>
    internal static Task StopExecutableAsync(string executable, CancellationToken cancellationToken = default)
        => StopExecutableAsync(executable, null, cancellationToken);

    /// <summary>サービス停止要求の前に対象を確保し、列挙から消えた実体も終了まで待ちます。</summary>
    /// <returns>対象プロセスの終了確認です。</returns>
    // 抑制理由: 列挙した全ハンドルは、停止待機の成功・失敗によらずfinallyで回収します。
    [SuppressMessage("SourceQuality", "RAW141", Justification = "停止要求前に確保したハンドルをfinallyまで保持します。")]
    internal static async Task StopExecutableAsync(string executable, Func<CancellationToken, Task>? requestStop, CancellationToken cancellationToken = default)
    {
        Process[] processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable));
        List<(Process Handle, ProcessIdentity Identity)> targets = [];
        try
        {
            foreach (Process process in processes)
            {
                if (!string.Equals(ProcessExecutable.GetPath(process), executable, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                _ = process.SafeHandle;
                if (!string.Equals(ProcessExecutable.GetPath(process), executable, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                targets.Add((process, ProcessOs.Identity(process)));
            }
            if (requestStop is not null)
            {
                await requestStop(cancellationToken).ConfigureAwait(false);
            }
            foreach ((Process process, ProcessIdentity identity) in targets)
            {
                EOutcome stopped = await ProcessOs.StopAsync(process, identity, EProcessStopPolicy.Force, (int)InstallationDefaults.StopTimeout.TotalMilliseconds,
                    new DefaultProcessPlatform(), cancellationToken).ConfigureAwait(false);
                if (stopped != EOutcome.Succeeded)
                {
                    throw new IOException("The client process could not be stopped. Its files were retained.");
                }
            }
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
        if (requestStop is not null)
        {
            // サービス停止中に起動した同じ配置の実体も、従来どおり停止対象に含めます。
            await StopExecutableAsync(executable, cancellationToken).ConfigureAwait(false);
        }
    }
}
