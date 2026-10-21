using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Scripting;

/// <summary>引数名から個別入力を作り、共通の開始入力や設定入力との衝突を防ぎます。</summary>
public static class ScriptInputPorts
{
    private static string Prefix => "$arguments.";

    /// <summary>未指定の保存済み定義は、従来のオブジェクト入力を維持します。</summary>
    /// <returns>個別入力が明示されているかです。</returns>
    public static bool Individual(JsonElement settings)
    {
        string key = "inputMode", individual = "individual";
        return settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty(key, out JsonElement mode)
            && mode.ValueKind == JsonValueKind.String && string.Equals(mode.GetString(), individual, StringComparison.Ordinal);
    }

    /// <summary>言語で使用可能な引数名を、予約ポートと区別します。</summary>
    /// <returns>保存・配線に使う入力識別子です。</returns>
    public static string Name(string argument) => Prefix + argument;

    /// <summary>表示と実行で同じ引数名を復元します。</summary>
    /// <returns>個別入力の引数名か、対象外ならnullです。</returns>
    public static string? Argument(string port)
    {
        ArgumentNullException.ThrowIfNull(port);
        return port.StartsWith(Prefix, StringComparison.Ordinal) ? port[Prefix.Length..]
                                                                 : null;
    }

    /// <summary>現在の解析に一致する引数だけを、選択した一方の入力方式で公開します。</summary>
    /// <returns>実行PCなどの共通入力を保持した定義です。</returns>
    public static NodeDescriptor Resolve(NodeDescriptor descriptor, NodeDefinition node)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(node);
        if (!ScriptSettings.IsScript(node.Type))
        {
            return descriptor;
        }
        ScriptContract? contract = ScriptSettings.Confirmed(node.Type, node.Settings)?.Contract;
        string arguments = "arguments";
        IEnumerable<PortDescriptor> inputs = [new(arguments, EPortValueKind.Json) { DataType = ScriptSettings.PortType(node, arguments, false) }];
        if (Individual(node.Settings))
        {
            string binary = "binary", csharp = "script.csharp", source = "script.csharp-source";
            inputs = contract?.Arguments.Select(argument =>
            {
                EPortValueKind kind = EPortValueKind.Json;

                // LEGACY-FORMAT: csharp-byte-array-string — 旧解析の入力にも実バイト列を届け、実行直前のC#解析で型を再確認します。
                if (string.Equals(argument.Value.Type, binary, StringComparison.Ordinal) || string.Equals(node.Type, csharp, StringComparison.Ordinal) || string.Equals(node.Type, source, StringComparison.Ordinal))
                {
                    kind = EPortValueKind.Any;
                }
                return new PortDescriptor(Name(argument.Key), kind, !argument.Value.Optional) { DataType = ScriptSettings.InputType(contract, argument.Key) };
            }) ?? [];
        }
        return descriptor with { Inputs = inputs.Concat(descriptor.Inputs.Where(port => !string.Equals(port.Name, arguments, StringComparison.Ordinal) && Argument(port.Name) is null)).ToArray() };
    }
}
