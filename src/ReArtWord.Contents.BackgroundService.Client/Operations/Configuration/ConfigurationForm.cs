using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Files;

namespace ReArtWord.Contents.BackgroundService.Client.Operations.Configuration;

/// <summary>共通設定フォームの入力を要求生成前に検証します。</summary>
internal static class ConfigurationForm
{
    /// <summary>表示中の文字列をHost集合へ確定し、項目ごとの不正を拒否します。</summary>
    /// <returns>送信可能な構造の共通設定です。</returns>
    internal static ClusterOperationalSettings Build(string timeZone, EFileConflictPolicy conflict, EFileInternalFailurePolicy failure, IEnumerable<(string Name, string Hosts)> groups)
    {
        if (string.IsNullOrWhiteSpace(timeZone))
        {
            throw UiError.InvalidOperation("Specify a time zone ID.");
        }
        Dictionary<string, IReadOnlyList<string>> hostGroups = new(StringComparer.Ordinal);
        int row = 0;
        foreach ((string name, string text) in groups)
        {
            row++;
            if (string.IsNullOrWhiteSpace(name))
            {
                throw UiError.InvalidOperation("Enter a name for Group {0}.", row);
            }
            string[] hosts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (hosts.Length == 0)
            {
                throw UiError.InvalidOperation("Enter one or more Host IDs for group {0}.", name);
            }
            if (!hostGroups.TryAdd(name.Trim(), hosts))
            {
                throw UiError.InvalidOperation("Group name {0} is duplicated.", name.Trim());
            }
        }
        return new(timeZone.Trim(), conflict, failure, hostGroups);
    }
}
