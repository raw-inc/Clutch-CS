namespace ReArtWord.Contents.BackgroundService.Infrastructure.Processes;

/// <summary>OSの登録場所と実行環境から、Shellの実行ファイル候補を調べます。</summary>
public static class TerminalShellDiscovery
{
    private static readonly string[] windowsNames = ["powershell.exe", "cmd.exe", "bash.exe", "sh.exe", "nu.exe"];
    private static readonly string[] unixNames = ["bash", "zsh", "fish", "dash", "ksh", "tcsh", "csh", "nu", "pwsh", "sh"];

    /// <summary>OSとPATHを参照します。実行タイミングと結果の寿命は呼出元が所有します。</summary>
    /// <returns>優先順の実在するShellパスです。</returns>
    public static IReadOnlyList<string> Discover() => Collect(Candidates());

    /// <summary>有限の候補を検査し、重複と非実行ファイルを取り除きます。</summary>
    /// <returns>この収集で確定した不変の一覧です。</returns>
    internal static IReadOnlyList<string> Collect(IEnumerable<string> candidates)
    {
        StringComparer comparer = StringComparer.Ordinal;
        if (OperatingSystem.IsWindows())
        {
            comparer = StringComparer.OrdinalIgnoreCase;
        }
        List<string> found = [];
        HashSet<string> seen = new(comparer);
        foreach (string candidate in candidates.Take(2048))
        {
            try
            {
                if (!Path.IsPathFullyQualified(candidate) || !File.Exists(candidate))
                {
                    continue;
                }
                string path = Path.GetFullPath(candidate);
                if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
                {
                    continue;
                }
                if (seen.Add(path))
                {
                    found.Add(path);
                }
                if (found.Count == 64)
                {
                    break;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // 読み取れない候補だけを除外し、他のShellとCore起動を妨げません。
            }
        }
        return found.AsReadOnly();
    }

    /// <summary>既存の既定優先順位を維持し、全ディスクの再帰探索は行いません。</summary>
    /// <returns>PATH・標準インストール場所・Unix登録一覧の候補です。</returns>
    private static List<string> Candidates()
    {
        string[] paths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
            .Select(path => path.Trim().Trim('"')).Where(Path.IsPathFullyQualified).Take(128).ToArray();
        List<string> candidates = [];
        if (OperatingSystem.IsWindows())
        {
            candidates.AddRange(WindowsCandidates(paths, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.SystemDirectory));
        }
        else
        {
            candidates.Add(Environment.GetEnvironmentVariable("SHELL") ?? string.Empty);
            candidates.Add("/bin/sh");
            try
            {
                string registry = "/etc/shells";
                if (File.Exists(registry))
                {
                    candidates.AddRange(File.ReadLines(registry).Take(256).Select(line => line.Split('#', 2)[0].Trim()).Where(Path.IsPathFullyQualified));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 登録一覧が利用不能でもSHELL・PATH・/bin/shを確認します。
            }
            candidates.AddRange(unixNames.SelectMany(name => paths.Select(path => Path.Combine(path, name))));
        }
        return candidates;
    }

    /// <summary>既定優先順位を維持し、サービスのPATHにないStore版は追加の選択肢にします。</summary>
    /// <returns>存在確認前の実行ファイル候補です。</returns>
    internal static IReadOnlyList<string> WindowsCandidates(string[] paths, string programFiles, string systemDirectory)
    {
        List<string> candidates = paths.Select(path => Path.Combine(path, "pwsh.exe")).ToList();
        AddPowerShellInstallations(candidates, Path.Combine(programFiles, "PowerShell"), "*");
        candidates.Add(Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"));
        candidates.Add(Path.Combine(systemDirectory, "cmd.exe"));
        candidates.AddRange(windowsNames.SelectMany(name => paths.Select(path => Path.Combine(path, name))));
        candidates.Add(Path.Combine(programFiles, "Git", "bin", "bash.exe"));
        candidates.Add(Path.Combine(programFiles, "nu", "bin", "nu.exe"));

        // Store版の直接起動は利用者のパッケージ権限にも依存するため、既存の既定Shellを変えません。
        AddPowerShellInstallations(candidates, Path.Combine(programFiles, "WindowsApps"), "Microsoft.PowerShell_7.*");
        return candidates.AsReadOnly();
    }

    /// <summary>一つの標準配置先だけを有限に探索し、読めない場所の影響を限定します。</summary>
    private static void AddPowerShellInstallations(List<string> candidates, string directory, string pattern)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                candidates.AddRange(Directory.EnumerateDirectories(directory, pattern).Take(32)
                    .OrderDescending(StringComparer.OrdinalIgnoreCase).Select(path => Path.Combine(path, "pwsh.exe")));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 読めない配置先だけを除外し、PATHと他の標準配置の探索を継続します。
        }
    }
}
