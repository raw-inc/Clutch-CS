using System.Collections;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Runtime.Processes.Commands;

namespace ReArtWord.Contents.BackgroundService.Nodes.Scripting.JavaScript;

/// <summary>明示したNode.jsで、環境変数の暗黙ロードとJSONの精度損失を防ぎます。</summary>
internal static class JavaScriptRuntime
{
    /// <summary>一回のNode.jsにだけ、起動コード・npm設定の継承除去を適用します。</summary>
    /// <returns>親プロセスを変更しない実行制限です。</returns>
    internal static ProcessRunLimits Limits(int output, long memory, string? directory = null, string? executable = null)
    {
        Dictionary<string, string?> environment = new(StringComparer.OrdinalIgnoreCase)
        {
            ["NODE_OPTIONS"] = null,
            ["NODE_PATH"] = null,
        };
        string prefix = "npm_config_";
        foreach (DictionaryEntry variable in System.Environment.GetEnvironmentVariables())
        {
            if (((string)variable.Key).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                environment[(string)variable.Key] = null;
            }
        }
        if (executable is not null)
        {
            environment["PATH"] = Path.GetDirectoryName(executable) + Path.PathSeparator + System.Environment.GetEnvironmentVariable("PATH");
        }
        return new(output, true, memory, directory, OutputEncoding: System.Text.Encoding.UTF8, EnvironmentOverrides: environment);
    }

    /// <summary>同期モジュール解決を持つNode.js 24以降の版を確認します。</summary>
    /// <returns>構成の固定に使用する完全なNode.js版です。</returns>
    internal static async Task<string> VersionAsync(string executable, CancellationToken cancellationToken = default)
    {
        string output = await BoundedProcessRunner.RunAsync(executable, ["--version"], Limits(1024, 268435456), cancellationToken).ConfigureAwait(false);
        string value = output.Trim().TrimStart('v');
        if (!Version.TryParse(value, out Version? version) || version.Major < 24)
        {
            throw new InvalidDataException("script.javascriptRuntimeRequired");
        }
        return value;
    }

    /// <summary>JSON整数がJavaScriptへの受渡しで別の値へ丸められる前に拒否します。</summary>
    internal static void ValidateNumbers(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            double number = value.GetDouble();
            if (!double.IsFinite(number) || Math.Truncate(number) == number && 9007199254740991d < Math.Abs(number))
            {
                throw new InvalidDataException("script.javascriptUnsafeNumber");
            }
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
            {
                ValidateNumbers(item);
            }
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in value.EnumerateObject())
            {
                ValidateNumbers(property.Value);
            }
        }
    }
}
