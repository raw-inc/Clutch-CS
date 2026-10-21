namespace ReArtWord.Contents.BackgroundService.Nodes.Scripting.Runtimes;

/// <summary>Peerが明示保存したパスだけを保持し、検出結果を無条件に永続化しません。</summary>
internal sealed record ScriptRuntimeConfiguration(string? Revision, IReadOnlyList<string> Paths, string? DefaultPath,
    IReadOnlyDictionary<string, string>? LegacyPaths = null)
{
    /// <summary>保存済みWFの明示パスも、確認済みの移行先へ対応させます。</summary>
    /// <returns>移行済みパス、または元の明示パスです。</returns>
    internal string Resolve(string path) => LegacyPaths?.GetValueOrDefault(path)
        ?? LegacyPaths?.FirstOrDefault(item => ScriptRuntimePaths.Comparer.Equals(item.Key, path)).Value ?? path;

    /// <summary>利用者の現在の選択を維持し、確認済みの旧パスだけを置き換えます。</summary>
    /// <returns>選択の意味と受付済みinstallの版を維持した移行設定です。</returns>
    internal ScriptRuntimeConfiguration Migrate(IReadOnlyDictionary<string, string> paths)
    {
        Dictionary<string, string> aliases = new(ScriptRuntimePaths.Comparer);
        foreach (KeyValuePair<string, string> item in LegacyPaths ?? new Dictionary<string, string>())
        {
            aliases[item.Key] = item.Value;
        }
        bool changed = false;
        foreach ((string oldPath, string newPath) in paths)
        {
            changed |= !aliases.TryGetValue(oldPath, out string? existing) || !ScriptRuntimePaths.Comparer.Equals(existing, newPath);
            aliases[oldPath] = newPath;
        }
        if (!changed)
        {
            return this;
        }
        string? selected = DefaultPath;
        if (selected is not null)
        {
            selected = aliases.GetValueOrDefault(selected, selected);
        }
        return new(Revision, Paths.Select(path => aliases.GetValueOrDefault(path, path)).Distinct(ScriptRuntimePaths.Comparer).ToArray(), selected, aliases);
    }
}
