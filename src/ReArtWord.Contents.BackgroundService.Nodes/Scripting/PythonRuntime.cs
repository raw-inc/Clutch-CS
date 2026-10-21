using ReArtWord.Contents.BackgroundService.Runtime.Processes.Commands;

namespace ReArtWord.Contents.BackgroundService.Nodes.Scripting;

/// <summary>既存のPython起動経路でも、ランチャーによる暗黙の追加導入を禁止します。</summary>
internal static class PythonRuntime
{
    /// <summary>環境変数は所有する子プロセスだけへ適用し、OSや親プロセスは変更しません。</summary>
    /// <returns>自動インストールを無効にした有限実行設定です。</returns>
    internal static ProcessRunLimits Limits(int output, long memory, string? directory = null, bool requireOutput = true)
        => new(output, requireOutput, memory, directory, EnvironmentOverrides: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["PYTHON_MANAGER_AUTOMATIC_INSTALL"] = "false",
            ["PYLAUNCHER_ALLOW_INSTALL"] = null,
            ["PYLAUNCHER_ALWAYS_INSTALL"] = null,
        });
}
