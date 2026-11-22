using System.Diagnostics;
using System.Globalization;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using ReArtWord.Contents.BackgroundService.Runtime.Processes.Commands;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Installation;

/// <summary>起動したCoreのOS実体を、インストール後のReady応答と照合します。</summary>
public static class InstallationLaunchIdentity
{
    /// <summary>起動時に取得した実体と応答の実体を照合します。旧Installerには登録前の日時による従来の検査を使います。</summary>
    /// <returns>実行中のプロセス、正確な開始時刻、配置した実行ファイルがすべて一致するかです。</returns>
    public static bool Matches(ProcessIdentity reported, string entry, ProcessIdentity? expected, DateTimeOffset? startedAfter)
    {
        ArgumentNullException.ThrowIfNull(reported);
        if (expected is not null && expected != reported || expected is null && (startedAfter is null || reported.StartedAt < startedAfter))
        {
            return false;
        }
        using Process? process = ProcessOs.Open(reported);
        StringComparison comparison = StringComparison.Ordinal;
        if (OperatingSystem.IsWindows())
        {
            comparison = StringComparison.OrdinalIgnoreCase;
        }
        return process is not null && !process.HasExited && string.Equals(process.MainModule?.FileName, entry, comparison);
    }

    /// <summary>OSが起動要求を受け付けた後、登録したサービスの主プロセスを確認します。</summary>
    /// <returns>Windowsサービス、systemdユニット、またはlaunchdジョブが所有する実体です。</returns>
    public static async Task<ProcessIdentity> WaitForServiceAsync(string name, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(InstallationDefaults.ReadyTimeout);
        int pid = await ServicePidAsync(name, limit.Token).ConfigureAwait(false);
        while (pid == 0)
        {
            await Task.Delay(InstallationDefaults.PollInterval, limit.Token).ConfigureAwait(false);
            pid = await ServicePidAsync(name, limit.Token).ConfigureAwait(false);
        }
        using Process process = Process.GetProcessById(pid);
        return ProcessOs.Identity(process);
    }

    /// <summary>対象サービスのPIDをOSに照会します。停止中または起動待ちのPIDは0です。</summary>
    /// <returns>サービス管理機構が報告した主プロセスのPIDです。</returns>
    internal static async Task<int> ServicePidAsync(string name, CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsLinux())
        {
            string output = await BoundedProcessRunner.RunAsync("/usr/bin/systemctl", ["show", "--property=MainPID", "--value", name + ".service"], cancellationToken).ConfigureAwait(false);
            return int.Parse(output.Trim(), NumberStyles.None, CultureInfo.InvariantCulture);
        }
        bool windows = OperatingSystem.IsWindows();
        string status;
        if (windows)
        {
            string command = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe");
            status = await BoundedProcessRunner.RunAsync(command, ["queryex", name], cancellationToken).ConfigureAwait(false);
        }
        else
        {
            status = await BoundedProcessRunner.RunAsync("/bin/launchctl", ["print", "system/" + name], cancellationToken).ConfigureAwait(false);
        }
        char separator = windows ? ':'
                                 : '=';
        string pidField = "pid";
        foreach (string line in status.Split('\n'))
        {
            int split = line.IndexOf(separator, StringComparison.Ordinal);
            if (0 < split && line.AsSpan(0, split).Trim().Equals(pidField, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line.AsSpan(split + 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
            {
                return pid;
            }
        }
        if (windows)
        {
            throw new InvalidDataException("サービスのPIDを確認できません。");
        }
        return 0;
    }
}
