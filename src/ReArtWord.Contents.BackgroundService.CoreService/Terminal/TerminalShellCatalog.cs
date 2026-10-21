using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Terminal;

namespace ReArtWord.Contents.BackgroundService.CoreService.Terminal;

/// <summary>起動時のShell候補を固定し、セッション作成の選択を検証します。</summary>
internal sealed class TerminalShellCatalog
{
    /// <summary>収集結果をCore実体が所有する不変の契約値にします。</summary>
    internal TerminalShellCatalog(IReadOnlyList<string> paths)
    {
        Shells = Array.AsReadOnly(paths.Select(path => new TerminalShell(Path.GetFileNameWithoutExtension(path), path)).ToArray());
    }

    /// <summary>先頭を既定とする、Core実体の寿命中に変化しない一覧です。</summary>
    internal IReadOnlyList<TerminalShell> Shells
    {
        get;
    }

    /// <summary>起動時に確認した同じShellだけを選び、OSの再探索は行いません。</summary>
    /// <returns>要求に対応する実行ファイルの絶対パスです。</returns>
    internal string Resolve(string? requested)
    {
        if (requested is null)
        {
            if (Shells.Count == 0)
            {
                throw new InvalidOperationException("No executable shell was found at Core startup.");
            }
            return Shells[0].Path;
        }
        StringComparison comparison = StringComparison.Ordinal;
        if (OperatingSystem.IsWindows())
        {
            comparison = StringComparison.OrdinalIgnoreCase;
        }
        return Shells.FirstOrDefault(shell => string.Equals(shell.Path, requested, comparison))?.Path
            ?? throw new ArgumentException("Select a shell from this Core's startup catalog.", nameof(requested));
    }
}
