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
    // 抑制理由: OSのprocess起動時刻を直ちにDateTimeOffsetへ変換してPID再利用を区別します。
    [SuppressMessage("SourceQuality", "RAW171", Justification = "BCLの起動時刻をPID再利用防止のDateTimeOffsetへ変換します。")]
    internal static async Task StopExecutableAsync(string executable, CancellationToken cancellationToken = default)
    {
        foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
        {
            using (process)
            {
                if (!string.Equals(ProcessExecutable.GetPath(process), executable, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                ProcessIdentity identity = new(process.Id, new DateTimeOffset(process.StartTime.ToUniversalTime()));
                EOutcome stopped = await ProcessOs.StopAsync(identity, EProcessStopPolicy.Force, (int)InstallationDefaults.StopTimeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
                if (stopped != EOutcome.Succeeded)
                {
                    throw new IOException("The client process could not be stopped. Its files were retained.");
                }
            }
        }
    }
}
