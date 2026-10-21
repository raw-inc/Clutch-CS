using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Installation;

/// <summary>Firewall 操作の入力と記録を保存し、後片付けで導入先を再作成しないようにします。</summary>
internal static class WindowsFirewallFiles
{
    private static string FilePrefix => "firewall-";
    private static string RulePrefix => "Clutch-CS-";
    private static string ResultSuffix => "-SetupResult";
    private static string GuidFormat => "N";

    /// <summary>一時ルールの記録は、削除対象の外にある操作専用フォルダーへ保存します。</summary>
    /// <returns>作成した設定ファイルのパスです。</returns>
    internal static async Task<string> WriteAsync(InstallationRecord record, bool remove, Guid? resultOperation, CancellationToken cancellationToken = default)
    {
        string directory = Path.Combine(InstallationFiles.Root(record.Settings.Root), "logs");
        string rule = record.ServiceName + "-Network";
        if (resultOperation.HasValue)
        {
            directory = Path.Combine(SetupWorkspace.DirectoryFor(resultOperation.Value), "firewall");
            rule = RulePrefix + resultOperation.Value.ToString("N") + ResultSuffix;
        }
        Directory.CreateDirectory(directory);
        string specification = Path.Combine(directory, FilePrefix + Guid.NewGuid().ToString("N") + ".json");
        await InstallationFiles.WriteAsync(specification, new
        {
            Name = rule, record.Settings.Network.InterfaceId, record.Settings.Network.BindAddress,
            Ports = new[] { record.Settings.Network.Port, InstallationDefaults.ManagementPort }.Distinct().ToArray(), Remove = remove,
            CorePath = Path.Combine(record.Settings.Root, "core", record.CoreEntryPoint), IncludeApplication = !resultOperation.HasValue,
        }, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.ChangeExtension(specification, ".ps1"), ReadScript(), cancellationToken).ConfigureAwait(false);
        return specification;
    }

    /// <summary>一時ルール削除の記録だけが残る配置先を判定し、ほかのファイルは受け入れません。</summary>
    /// <returns>ログを保持したまま新規導入できるかです。</returns>
    internal static bool ContainsOnlyCleanupLogs(string root)
    {
        string logs = Path.Combine(root, "logs");
        string[] entries = Directory.GetFileSystemEntries(root);
        if (entries.Length != 1 || !string.Equals(entries[0], logs, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(logs))
        {
            return false;
        }
        _ = InstallationFiles.Root(logs);
        HashSet<string> prefixes = new(StringComparer.OrdinalIgnoreCase);
        string[] suffixes = [".json.error.txt", ".json", ".ps1"];
        foreach (string entry in Directory.EnumerateFileSystemEntries(logs))
        {
            _ = InstallationFiles.Root(entry);
            string name = Path.GetFileName(entry);
            string? suffix = suffixes.FirstOrDefault(value => name.EndsWith(value, StringComparison.OrdinalIgnoreCase));
            if (Directory.Exists(entry) || suffix is null || !name.StartsWith(FilePrefix, StringComparison.Ordinal)
                || !Guid.TryParseExact(name.AsSpan(FilePrefix.Length, name.Length - FilePrefix.Length - suffix.Length), GuidFormat, out _))
            {
                return false;
            }
            prefixes.Add(entry[..^suffix.Length]);
        }
        string lineEnding = "\n";
        string script = ReadScript().ReplaceLineEndings(lineEnding);
        foreach (string prefix in prefixes)
        {
            string specification = prefix + ".json";
            string scriptPath = prefix + ".ps1";
            if (!IsCleanupSpecification(specification, root) || !File.Exists(scriptPath)
                || !string.Equals(File.ReadAllText(scriptPath).ReplaceLineEndings(lineEnding), script, StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>この導入先の一時ルール削除で作られた設定かを確認します。</summary>
    /// <returns>削除記録として一致するかです。</returns>
    private static bool IsCleanupSpecification(string path, string root)
    {
        if (!File.Exists(path))
        {
            return false;
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement value = document.RootElement;
            string? name = value.GetProperty("name").GetString();
            string? core = value.GetProperty("corePath").GetString();
            return value.GetProperty("remove").ValueKind == JsonValueKind.True
                && value.GetProperty("includeApplication").ValueKind == JsonValueKind.False
                && name is not null && RulePrefix.Length + ResultSuffix.Length < name.Length
                && name.StartsWith(RulePrefix, StringComparison.Ordinal) && name.EndsWith(ResultSuffix, StringComparison.Ordinal)
                && Guid.TryParseExact(name.AsSpan(RulePrefix.Length, name.Length - RulePrefix.Length - ResultSuffix.Length), GuidFormat, out _)
                && core is not null && Path.IsPathFullyQualified(core) && UpdateLayout.Contains(Path.Combine(root, "core"), core);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>配布物に埋め込んだ Firewall スクリプトを読み取ります。</summary>
    /// <returns>スクリプトの本文です。</returns>
    private static string ReadScript()
    {
        using Stream source = typeof(WindowsFirewallFiles).Assembly.GetManifestResourceStream(typeof(WindowsFirewallFiles).Namespace + ".WindowsFirewall.ps1")!;
        using StreamReader reader = new(source);
        return reader.ReadToEnd();
    }
}
