using ReArtWord.Contents.BackgroundService.Contracts.Scripting.Runtimes;
using ReArtWord.Contents.BackgroundService.Nodes.Scripting.JavaScript;
using ReArtWord.Contents.BackgroundService.Nodes.Scripting.Packages;

namespace ReArtWord.Contents.BackgroundService.Nodes.Scripting.Runtimes;

/// <summary>言語名、実行先と対応バージョンの共通検証を所有します。</summary>
internal static class ScriptRuntimePaths
{
    /// <summary>OSのファイル名規則に合わせてパスを比較します。</summary>
    internal static StringComparer Comparer => OperatingSystem.IsWindows() switch { true => StringComparer.OrdinalIgnoreCase, false => StringComparer.Ordinal };

    /// <summary>管理対象の二言語だけを受け入れます。</summary>
    /// <returns>正規化された言語名です。</returns>
    internal static string Language(string language) => language switch
    {
        "python" => "python",
        "javascript" => "javascript",
        _ => throw new ArgumentException("script.runtime.language", nameof(language)),
    };

    /// <summary>空欄は既定選択とし、相対パスやコマンド文字列を受け入れません。</summary>
    /// <returns>絶対パス、または既定選択です。</returns>
    internal static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        if (4096 < path.Length || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("script.runtime.absolutePathRequired", nameof(path));
        }
        return Path.GetFullPath(path.Trim());
    }

    /// <summary>任意のコードを読み込まず、対応版を有限時間で確認します。</summary>
    /// <returns>利用可否と版を持つ一覧項目です。</returns>
    internal static async Task<ScriptRuntimeEntry> ProbeAsync(string language, string path, string source, bool registered, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length == 0 || IsPythonLauncher(path))
            {
                return new(path, null, source, registered, false, "script.runtime.unavailable");
            }
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            string python = "python";
            string version;
            if (string.Equals(language, python, StringComparison.Ordinal))
            {
                version = await PipScriptPackages.VersionAsync(path, timeout.Token).ConfigureAwait(false);
            }
            else
            {
                version = await JavaScriptRuntime.VersionAsync(path, timeout.Token).ConfigureAwait(false);
            }
            return new(path, version, source, registered, true);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(path, null, source, registered, false, "script.runtime.incompatibleOrUnavailable");
        }
    }

    /// <summary>Windowsの導入マネージャーを実行環境と誤認せず、配置済みの実体を候補にします。</summary>
    /// <returns>Python導入マネージャーまたはその可変エイリアスかです。</returns>
    internal static bool IsPythonLauncher(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }
        string manager = "manage";
        return string.Equals(System.Diagnostics.FileVersionInfo.GetVersionInfo(path).ProductName, manager, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>ログイン利用者のホーム配下に言語別の保存先を固定します。</summary>
    /// <returns>バージョン別ディレクトリを配置する親です。</returns>
    internal static string InstallRoot(ScriptRuntimeUser user, string language)
    {
        string leaf = Language(language) switch { "python" => "py", _ => "js" };
        string home = Normalize(user.HomeDirectory) ?? throw new InvalidOperationException("script.runtime.userUnavailable");
        return Path.Combine(home, ".clutch-cs", "runtime", leaf);
    }
}
