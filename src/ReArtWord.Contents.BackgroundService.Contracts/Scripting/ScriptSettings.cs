using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Scripting;

/// <summary>コード変更後に古い型表示を採用しないための共通契約読取りです。</summary>
public static class ScriptSettings
{
    /// <summary>通常ノードから解析対象だけを取り出します。</summary>
    /// <returns>実行制限や前の診断を含めない解析要求です。</returns>
    public static ScriptAnalysisRequest Request(string type, JsonElement settings)
    {
        string language = type switch { "script.csharp" or "script.csharp-source" => "csharp", "script.python" => "python", "script.javascript" => "javascript", _ => string.Empty };
        bool block = type is "value.code";
        string languageKey = "language";
        if (block && settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty(languageKey, out JsonElement selected) && selected.ValueKind == JsonValueKind.String)
        {
            language = selected.GetString() ?? string.Empty;
        }
        string code = "code", manual = "manualContract", python = "pythonPath", node = "nodePath";
        JsonElement contract = default;
        string? executable = null, nodePath = null;
        bool dynamic = language is "python" or "javascript";
        if (dynamic)
        {
            settings.TryGetProperty(manual, out contract);
        }
        string pythonLanguage = "python";
        if (string.Equals(language, pythonLanguage, StringComparison.Ordinal))
        {
            if (settings.TryGetProperty(python, out JsonElement value))
            {
                executable = value.GetString();
            }
        }
        string javascriptLanguage = "javascript";
        if (string.Equals(language, javascriptLanguage, StringComparison.Ordinal) && settings.TryGetProperty(node, out JsonElement runtime))
        {
            nodePath = runtime.GetString();
        }
        string text = string.Empty;
        if (settings.TryGetProperty(code, out JsonElement source))
        {
            text = source.GetString() ?? string.Empty;
        }
        return new(language, text, contract, executable, block, nodePath, Continuous: type is "script.csharp-source");
    }

    /// <summary>保存・描画・配線で同じスクリプトノード群を判定します。</summary>
    /// <returns>型付き自由記述ノードかどうかです。</returns>
    public static bool IsScript(string type) => type is "script.csharp" or "script.csharp-source" or "script.python" or "script.javascript";

    /// <summary>型表示は現在のコード・明示型・外部ランタイムに一致する解析だけを使います。</summary>
    /// <returns>一致する契約か、未確認ならnullです。</returns>
    public static ScriptAnalysis? Latest(string type, JsonElement settings)
    {
        if (settings.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        try
        {
            string key = "analysis";
            if (settings.TryGetProperty(key, out JsonElement stored))
            {
                ScriptAnalysis? report = stored.Deserialize<ScriptAnalysis>(ContractJson.Options);
                if (report is not null && string.Equals(report.Fingerprint, ContractJson.Hash(Request(type, settings)), StringComparison.Ordinal))
                {
                    return report;
                }
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
        return null;
    }

    /// <summary>現在のコードに対して型検証も成功した解析だけを使用します。</summary>
    /// <returns>型契約を持つ成功結果かnullです。</returns>
    public static ScriptAnalysis? Confirmed(string type, JsonElement settings)
    {
        ScriptAnalysis? report = Latest(type, settings);
        if (report?.Contract is not null && report.Diagnostics is { Count: 0 })
        {
            return report;
        }
        return null;
    }

    /// <summary>引数オブジェクトと返却値の形状を接続画面へ公開します。</summary>
    /// <returns>検査済みコードの型だけを返します。</returns>
    public static ValueTypeDefinition? PortType(NodeDefinition node, string port, bool outbound)
    {
        ArgumentNullException.ThrowIfNull(node);
        bool supported = IsScript(node.Type);
        if (!supported)
        {
            return null;
        }
        ScriptContract? contract = Confirmed(node.Type, node.Settings)?.Contract;
        if (!outbound && ScriptInputPorts.Individual(node.Settings))
        {
            return ScriptInputPorts.Argument(port) is { } argument ? InputType(contract, argument)
                                                                   : null;
        }
        return (outbound, port) switch { (true, "value") => contract?.Result, (false, "arguments") when contract is not null => new("object", Properties: contract.Arguments.ToDictionary(pair => pair.Key, pair => JsonType(InputType(contract, pair.Key)!), StringComparer.Ordinal)), _ => null };
    }

    /// <summary>一括引数とworker通信では、ポート直下のバイナリだけをBase64文字列で表現します。</summary>
    /// <returns>入れ子のJSON型を変更しない通信境界の型です。</returns>
    public static ValueTypeDefinition JsonType(ValueTypeDefinition type)
    {
        ArgumentNullException.ThrowIfNull(type);
        string binary = "binary";
        if (string.Equals(type.Type, binary, StringComparison.Ordinal))
        {
            return type with { Type = "string" };
        }
        return type;
    }

    /// <summary>可変長引数は単一値も受け取り、実行直前に要素型を検証します。</summary>
    /// <returns>編集と配線で受け付ける入力型です。</returns>
    public static ValueTypeDefinition? InputType(ScriptContract? contract, string argument)
    {
        if (contract?.VariadicArgument is { } variadic && string.Equals(argument, variadic, StringComparison.Ordinal))
        {
            return new("any", Optional: true);
        }
        return contract?.Arguments.GetValueOrDefault(argument);
    }

    /// <summary>旧版で必須とされた既定値付き引数だけは、任意入力への緩和を許容します。</summary>
    /// <returns>返却型・要素型・引数名を変えずに実行できる契約かです。</returns>
    public static bool Matches(ScriptContract expected, ScriptContract actual, bool legacyBinary = false)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        ScriptContract compatible = actual with { Result = ScriptContractCompatibility.CompareAs(expected.Result, actual.Result, legacyBinary), Arguments = actual.Arguments.ToDictionary(pair => pair.Key, pair => ScriptContractCompatibility.CompareAs(expected.Arguments.GetValueOrDefault(pair.Key), pair.Value, legacyBinary) with
        {
            Optional = pair.Value.Optional && expected.Arguments.GetValueOrDefault(pair.Key)?.Optional == true,
        }, StringComparer.Ordinal) };
        return string.Equals(ContractJson.Hash(expected), ContractJson.Hash(compatible), StringComparison.Ordinal);
    }
}
