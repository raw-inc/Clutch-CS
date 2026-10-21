using System.Security;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Runtime.Processes.Commands;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Installation;

/// <summary>Linux/macOSで利用可能な自動起動方式を提供します。</summary>
internal static class UnixInstallation
{
    /// <summary>OSごとの登録ファイルを新規作成します。</summary>
    /// <returns>登録完了です。</returns>
    internal static async Task RegisterAsync(StartupRegistration record, CancellationToken cancellationToken = default)
    {
        string path = RegistrationPath(record);
        if (File.Exists(path))
        {
            VerifyOwnership(record);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using StreamWriter writer = new(stream);
        await writer.WriteAsync(RegistrationText(record).AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (OperatingSystem.IsLinux() && !record.PerUser)
        {
            await RunAsync("/usr/bin/systemctl", ["daemon-reload"], cancellationToken).ConfigureAwait(false);
            await RunAsync("/usr/bin/systemctl", ["enable", record.Name + ".service"], cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>登録済みsystem serviceを起動します。</summary>
    /// <returns>起動要求完了です。</returns>
    internal static Task StartAsync(StartupRegistration record, CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsLinux())
        {
            return RunAsync("/usr/bin/systemctl", ["start", record.Name + ".service"], cancellationToken);
        }
        return RunAsync("/bin/launchctl", ["bootstrap", "system", RegistrationPath(record)], cancellationToken);
    }

    /// <summary>登録済みsystem serviceを停止します。</summary>
    /// <returns>停止要求完了です。</returns>
    internal static Task StopAsync(StartupRegistration record, CancellationToken cancellationToken = default)
    {
        VerifyOwnership(record);
        if (OperatingSystem.IsLinux())
        {
            return RunAsync("/usr/bin/systemctl", ["stop", record.Name + ".service"], cancellationToken);
        }
        return RunAsync("/bin/launchctl", ["bootout", "system/" + record.Name], cancellationToken);
    }

    /// <summary>自身の登録ファイルを削除します。</summary>
    /// <returns>削除完了です。</returns>
    internal static async Task UnregisterAsync(StartupRegistration record, CancellationToken cancellationToken = default)
    {
        VerifyOwnership(record);
        if (OperatingSystem.IsLinux() && !record.PerUser)
        {
            await RunAsync("/usr/bin/systemctl", ["disable", record.Name + ".service"], cancellationToken).ConfigureAwait(false);
        }
        File.Delete(RegistrationPath(record));
        if (OperatingSystem.IsLinux() && !record.PerUser)
        {
            await RunAsync("/usr/bin/systemctl", ["daemon-reload"], cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>OSごとの固定登録先です。</summary>
    /// <returns>絶対パスです。</returns>
    private static string RegistrationPath(StartupRegistration record)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsLinux())
        {
            if (record.PerUser)
            {
                return Path.Combine(home, ".config", "autostart", record.Name + ".desktop");
            }
            return "/etc/systemd/system/" + record.Name + ".service";
        }
        if (OperatingSystem.IsMacOS())
        {
            if (record.PerUser)
            {
                return Path.Combine(home, "Library", "LaunchAgents", record.Name + ".plist");
            }
            return "/Library/LaunchDaemons/" + record.Name + ".plist";
        }
        throw new PlatformNotSupportedException("This operating system has no automatic startup adapter.");
    }

    /// <summary>任意shell展開を含まない登録内容を生成します。</summary>
    /// <returns>unit、desktop entryまたはplistです。</returns>
    private static string RegistrationText(StartupRegistration record)
    {
        string executable = record.Executable;
        string arguments = string.Concat(record.Arguments.Select(argument => "<string>" + SecurityElement.Escape(argument) + "</string>"));
        if (OperatingSystem.IsMacOS())
        {
            string keepAlive = "";
            if (!record.PerUser)
            {
                keepAlive = "<key>KeepAlive</key><dict><key>SuccessfulExit</key><false/></dict>";
            }
            return $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><plist version=\"1.0\"><dict><key>Label</key><string>{record.Name}</string><key>ProgramArguments</key><array><string>{SecurityElement.Escape(executable)}</string>{arguments}</array><key>RunAtLoad</key><true/>{keepAlive}</dict></plist>";
        }
        string command = Quote(executable) + " " + string.Join(" ", record.Arguments.Select(QuoteArgument));
        if (record.PerUser)
        {
            return "[Desktop Entry]\nType=Application\nName=Clutch-CS\nExec=" + command + "\nTerminal=false\n";
        }
        return "[Unit]\nDescription=Clutch-CS\nAfter=network-online.target\nStartLimitIntervalSec=0\n[Service]\nType=notify\nExecStart=" + command + "\nRestart=on-failure\nRestartSec=10s\n[Install]\nWantedBy=multi-user.target\n";
    }

    /// <summary>別用途の登録ファイルを削除しません。</summary>
    private static void VerifyOwnership(StartupRegistration record)
    {
        string path = RegistrationPath(record);
        if (File.Exists(path) && !string.Equals(File.ReadAllText(path), RegistrationText(record), StringComparison.Ordinal))
        {
            throw new IOException("The startup registration has changed. Review it before removal.");
        }
    }

    /// <summary>既存のCore登録と同じ引用方法を維持します。</summary>
    /// <returns>引用済み引数です。</returns>
    private static string QuoteArgument(string value)
    {
        string prefix = "--";
        if (value.StartsWith(prefix, StringComparison.Ordinal))
        {
            return value;
        }
        return Quote(value);
    }

    /// <summary>systemd/desktop entryの引用規則へ変換します。</summary>
    /// <returns>引用済み引数です。</returns>
    private static string Quote(string text) => "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("%", "%%", StringComparison.Ordinal).Replace("$", "\\$", StringComparison.Ordinal).Replace("`", "\\`", StringComparison.Ordinal) + "\"";

    /// <summary>管理commandへ有限時間を設定します。</summary>
    /// <returns>command完了です。</returns>
    private static async Task RunAsync(string executable, string[] arguments, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(InstallationDefaults.StopTimeout);
        _ = await BoundedProcessRunner.RunAsync(executable, arguments, timeout.Token).ConfigureAwait(false);
    }
}
