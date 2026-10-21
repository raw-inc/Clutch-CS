namespace ReArtWord.Contents.BackgroundService.Nodes.Scripting;

/// <summary>自己ホスト型とdotnetホスト型のCoreワーカー起動を統一します。</summary>
/// <param name="Executable">絶対パスの実行ファイルです。</param>
/// <param name="PrefixArguments">dotnet起動時のCore DLLなど固定の先頭引数です。</param>
public sealed record ScriptHostCommand(string Executable, IReadOnlyList<string> PrefixArguments)
{
    /// <summary>通常Core自身の実行方式を維持します。</summary>
    /// <returns>shellを必要としない起動情報です。</returns>
    public static ScriptHostCommand Current()
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("script.processPathUnavailable");
        string host = "dotnet";
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), host, StringComparison.OrdinalIgnoreCase))
        {
            return new(executable, [System.Reflection.Assembly.GetEntryAssembly()!.Location]);
        }
        return new(executable, []);
    }
}
