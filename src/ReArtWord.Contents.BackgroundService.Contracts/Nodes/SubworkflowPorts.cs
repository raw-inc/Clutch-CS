using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Nodes;

/// <summary>公開境界と固定した呼出し定義の型を、描画・配線・実行検証で一致させます。</summary>
public static class SubworkflowPorts
{
    /// <summary>境界ノードの型、または呼出し中の不変の契約へポートを解決します。</summary>
    /// <returns>不正設定は通常の検証へ渡し、表示を停止しません。</returns>
    public static NodeDescriptor Resolve(NodeDescriptor descriptor, NodeDefinition node)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(node);
        try
        {
            if (SubworkflowInterface.IsBoundary(node.Type) || string.Equals(node.Type, SubworkflowInterface.PortType, StringComparison.Ordinal))
            {
                SubworkflowField field = SubworkflowInterface.ReadField(node);
                bool output = !string.Equals(node.Type, SubworkflowInterface.InputType, StringComparison.Ordinal);
                string value = "value";
                EPortValueKind kind = SubworkflowInterface.Port(field, output).ValueKind;
                return descriptor with
                {
                    Inputs = descriptor.Inputs.Select(port => string.Equals(port.Name, value, StringComparison.Ordinal) ? port with { DataType = field.Type, ValueKind = kind, Required = output && field.DefaultValue is null }
                                                                                                                        : port).ToArray(),
                    Outputs = descriptor.Outputs.Select(port => string.Equals(port.Name, value, StringComparison.Ordinal) ? port with { DataType = field.Type, ValueKind = kind }
                                                                                                                          : port).ToArray(),
                };
            }
            if (SubworkflowInterface.Target(node.Type) is { } target && SubworkflowInterface.Captured(node) is { } definition
                && string.Equals(target, definition.Id, StringComparison.Ordinal))
            {
                SubworkflowInterface contract = SubworkflowInterface.Read(definition);
                return descriptor with
                {
                    Inputs = contract.Inputs.Select(field => SubworkflowInterface.Port(field, true)).Concat(descriptor.Inputs.Where(port => string.Equals(port.Name, Targets.PcSelectionPorts.Execution, StringComparison.Ordinal))).ToArray(),
                    Outputs = contract.Outputs.Select(field => SubworkflowInterface.Port(field, false)).ToArray(),
                };
            }
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException or InvalidOperationException)
        {
            // 保存や実行の検証が問題を表示します。編集だけは継続します。
        }
        return descriptor;
    }

    /// <summary>型推測を再帰して呼び直さず、明示された詳細型だけを読みます。</summary>
    /// <returns>宣言がある場合の具体型です。</returns>
    public static ValueTypeDefinition? PortType(NodeDescriptor? descriptor, NodeDefinition node, string port, bool outbound)
    {
        if (descriptor is null)
        {
            return null;
        }
        NodeDescriptor resolved = Resolve(descriptor, node);
        return (outbound ? resolved.Outputs
                         : resolved.Inputs).FirstOrDefault(item => string.Equals(item.Name, port, StringComparison.Ordinal))?.DataType;
    }
}
