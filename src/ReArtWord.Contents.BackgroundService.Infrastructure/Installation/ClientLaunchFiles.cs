using ReArtWord.Contents.BackgroundService.Contracts.Installation;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Installation;

/// <summary>クライアントの表示資材と明示起動用入口を配置します。</summary>
public static class ClientLaunchFiles
{
    /// <summary>クライアントの起動と案内で共通のURLを使用します。</summary>
    /// <returns>このPCで使用するHTTPのURLです。</returns>
    public static Uri GetUrl(int port) => new("http://127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/", UriKind.Absolute);

    /// <summary>Coreと独立したClientHostを利用者が明示起動する入口です。</summary>
    /// <returns>起動fileの配置完了です。</returns>
    public static async Task WriteAsync(InstallationRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Settings.WebClient is not null)
        {
            if (record.WebClient is not null)
            {
                await WriteLinkAsync(record.Settings.Root, record.WebClient, cancellationToken).ConfigureAwait(false);
            }
            return;
        }
        string executable = Path.Combine(record.Settings.Root, "client", record.ClientEntryPoint);
        string appearance = Path.Combine(record.Settings.Root, "config", "appearance.json");
        await WriteLauncherAsync(record.Settings.Root, executable, appearance, WebClientCatalog.LegacyPort(record.InstallationId), record.InstallationId, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>クライアント自身の設定だけで起動できる入口を保存します。</summary>
    /// <returns>独立した起動入口の保存です。</returns>
    public static async Task WriteClientAsync(WebClientInstallation client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (OperatingSystem.IsWindows() && client.AutomaticStartup && !client.RunAsService)
        {
            using Stream resource = typeof(ClientLaunchFiles).Assembly.GetManifestResourceStream("Clutch-CS.StartClient")!;
            using StreamReader reader = new(resource);
            await File.WriteAllTextAsync(Path.Combine(client.Root, "Start-Client.ps1"), await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }
        await WriteLauncherAsync(client.Root, Path.Combine(WebClientCatalog.ProgramRoot(client), client.EntryPoint), Path.Combine(client.Root, "config", "appearance.json"), client.Port, client.InstallationId, client, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>既存クライアントへの入口を作り、プログラムを複製しません。</summary>
    /// <returns>起動入口の保存です。</returns>
    public static async Task WriteLinkAsync(string root, WebClientInstallation client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        string directory = client.Root;
        if (!File.Exists(Path.Combine(directory, WebClientCatalog.RecordName)))
        {
            directory = Path.GetDirectoryName(directory)!;
        }
        if (OperatingSystem.IsWindows())
        {
            string target = Path.Combine(directory, "Open-Client.cmd").Replace("%", "%%", StringComparison.Ordinal);
            await File.WriteAllTextAsync(Path.Combine(root, "Open-Client.cmd"), "@echo off\r\n\"" + target + "\"\r\n", cancellationToken).ConfigureAwait(false);
            return;
        }
        string path = Path.Combine(root, "open-client.sh");
        await File.WriteAllTextAsync(path, "#!/bin/sh\nexec /bin/sh " + ShellQuote(Path.Combine(directory, "open-client.sh")) + "\n", cancellationToken).ConfigureAwait(false);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>同じクライアントID・port・表示設定で起動します。</summary>
    /// <returns>OS別の起動fileの保存です。</returns>
    private static async Task WriteLauncherAsync(string root, string executable, string appearance, int port, Guid clientId, WebClientInstallation? client, CancellationToken cancellationToken = default)
    {
        string url = GetUrl(port).OriginalString;
        string delay = InstallationDefaults.ClientOpenDelay.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string serviceArgument = string.Empty;
        if (client is { AutomaticStartup: true })
        {
            serviceArgument = " --service-name " + WebClientStartup.ServiceName(client);
        }
        if (OperatingSystem.IsWindows())
        {
            string command = "@echo off\r\nstart \"\" /b \"" + executable.Replace("%", "%%", StringComparison.Ordinal) + "\" --state \"%LOCALAPPDATA%\\ReArtWord\\Contents\\" + clientId.ToString("N") + "\" --appearance \"" + appearance.Replace("%", "%%", StringComparison.Ordinal) + "\" --listen " + url + serviceArgument + "\r\ntimeout /t " + delay + " /nobreak >nul\r\nstart \"\" " + url + "\r\n";
            if (client is { AutomaticStartup: true, RunAsService: false })
            {
                command = "@echo off\r\nschtasks /Run /TN \"" + WebClientStartup.ServiceName(client) + "\" >nul\r\ntimeout /t " + delay + " /nobreak >nul\r\nstart \"\" " + url + "\r\n";
            }
            if (client is { AutomaticStartup: true, RunAsService: true })
            {
                command = "@echo off\r\nstart \"\" " + url + "\r\n";
            }
            await File.WriteAllTextAsync(Path.Combine(root, "Open-Client.cmd"), command, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            string command = "#!/bin/sh\n" + ShellQuote(executable) + " --state \"$HOME/.local/share/Clutch-CS/" + clientId.ToString("N") + "\" --appearance " + ShellQuote(appearance) + " --listen " + ShellQuote(url) + serviceArgument + " >/dev/null 2>&1 &\nsleep " + delay + "\n";
            if (client is { AutomaticStartup: true, RunAsService: false })
            {
                command = "#!/bin/sh\n" + ShellQuote(executable) + " --state " + ShellQuote(WebClientStartup.StateDirectory(client)) + " --listen " + ShellQuote(url) + " >/dev/null 2>&1 &\nsleep " + delay + "\n";
            }
            if (client is { AutomaticStartup: true, RunAsService: true })
            {
                command = "#!/bin/sh\n";
            }
            string opener = "xdg-open ";
            if (OperatingSystem.IsMacOS())
            {
                opener = "open ";
            }
            string path = Path.Combine(root, "open-client.sh");
            await File.WriteAllTextAsync(path, command + opener + ShellQuote(url) + "\n", cancellationToken).ConfigureAwait(false);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    /// <summary>POSIX shellの単一引数へ固定します。</summary>
    /// <returns>引用済み引数です。</returns>
    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
}
