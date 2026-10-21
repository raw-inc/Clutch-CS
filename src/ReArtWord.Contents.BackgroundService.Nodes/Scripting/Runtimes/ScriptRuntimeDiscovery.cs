using ReArtWord.Contents.BackgroundService.Contracts.Scripting.Runtimes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Scripting.Runtimes;

/// <summary>PATHと利用者の標準導入先を浅く検索し、実行環境候補を有限件に限定します。</summary>
internal static class ScriptRuntimeDiscovery
{
    /// <summary>優先順を保持し、同じ実行ファイルを重複して検査しません。</summary>
    /// <returns>実行ファイルと検出元です。</returns>
    internal static IReadOnlyDictionary<string, string> Find(string language, ScriptRuntimeUser? user)
    {
        Dictionary<string, string> paths = new(ScriptRuntimePaths.Comparer);
        string python = "python";
        string[] names = ["node"];
        if (string.Equals(language, python, StringComparison.Ordinal))
        {
            names = ["python3", "python"];
        }
        if (OperatingSystem.IsWindows())
        {
            names = names.Select(name => name + ".exe").ToArray();
        }
        string search = user?.SearchPath + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        foreach (string directory in search.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(256))
        {
            AddDirectory(paths, directory.Trim('"'), names, "PATH");
        }
        if (user is not null)
        {
            AddChildren(paths, ScriptRuntimePaths.InstallRoot(user, language), names, "installed");
            if (OperatingSystem.IsWindows())
            {
                if (string.Equals(language, python, StringComparison.Ordinal))
                {
                    AddChildren(paths, Path.Combine(user.HomeDirectory, "AppData", "Local", "Programs", "Python"), names, "user");
                    AddChildren(paths, Path.Combine(user.HomeDirectory, "AppData", "Local", "Python"), names, "user");
                }
                else
                {
                    AddDirectory(paths, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs"), names, "system");
                }
            }
        }
        return paths;
    }

    /// <summary>空のStoreエイリアスを起動せず、存在する通常ファイルだけを候補にします。</summary>
    private static void AddDirectory(Dictionary<string, string> paths, string directory, string[] names, string source)
    {
        if (!Path.IsPathFullyQualified(directory))
        {
            return;
        }
        foreach (string name in names)
        {
            string path = Path.Combine(directory, name);
            string aliases = "WindowsApps";
            if (paths.Count < 64 && File.Exists(path) && !directory.Contains(aliases, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    if (!ScriptRuntimePaths.IsPythonLauncher(path))
                    {
                        paths.TryAdd(Path.GetFullPath(path), source);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                {
                    // 取得中に消えたファイルやアクセス不能な候補は起動しません。
                }
            }
        }
    }

    /// <summary>既知のバージョン別配置だけを読み、ホーム全体を再帰検索しません。</summary>
    private static void AddChildren(Dictionary<string, string> paths, string root, string[] names, string source)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return;
            }
            foreach (string directory in Directory.EnumerateDirectories(root).Take(64))
            {
                AddDirectory(paths, directory, names, source);
                AddDirectory(paths, Path.Combine(directory, "bin"), names, source);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 他利用者の領域が読めなくても、PATHと明示登録の候補は利用できます。
        }
    }
}
