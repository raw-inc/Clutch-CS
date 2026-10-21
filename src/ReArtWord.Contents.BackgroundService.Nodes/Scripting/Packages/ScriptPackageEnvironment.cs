using ReArtWord.Contents.BackgroundService.Contracts.Scripting.Packages;

namespace ReArtWord.Contents.BackgroundService.Nodes.Scripting.Packages;

/// <summary>一回の解析・実行へ渡す、後から書き換えないローカルの環境です。</summary>
public sealed record ScriptPackageEnvironment(ScriptPackageEnvironmentInfo? Info, IReadOnlyList<string> References,
    IReadOnlyList<string> Assemblies, IReadOnlyList<string> NativeLibraries, string? PythonExecutable = null, string? NpmDirectory = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? RuntimeExecutable = null)
{
    /// <summary>従来の標準参照と明示Pythonパスを使用する環境です。</summary>
    public static ScriptPackageEnvironment Empty
    {
        get;
    } = new(null, [], [], []);
}
