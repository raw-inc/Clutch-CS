using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Nodes;

/// <summary>設定から確定するポートを編集・コンパイル・実行で揃えます。</summary>
public static class ConfiguredNodePorts
{
    /// <summary>型定義では選択した構造に使う入力だけを公開します。</summary>
    /// <returns>設定に適合するポート定義です。</returns>
    public static NodeDescriptor? Resolve(NodeDescriptor? descriptor, NodeDefinition? node, WorkflowDefinition? document = null, Values.WorkflowTypeInference? inference = null, Values.WorkflowTypeInference? sharedInference = null)
        => ResolveCore(descriptor, node, document, inference, sharedInference, includePropertyOutputs: true);

    /// <summary>実行と入力検証に必要な契約を、描画用のプロパティ出力を推論せず取得します。</summary>
    /// <returns>動的入力、元の出力、開始・完了信号を維持した契約です。</returns>
    public static NodeDescriptor? ResolveExecution(NodeDescriptor? descriptor, NodeDefinition? node)
        => ResolveCore(descriptor, node, null, null, null, includePropertyOutputs: false);

    /// <summary>入力と信号の契約を共用し、編集・接続検証の場合だけ派生出力を追加します。</summary>
    /// <returns>指定用途に適合するポート定義です。</returns>
    private static NodeDescriptor? ResolveCore(NodeDescriptor? descriptor, NodeDefinition? node, WorkflowDefinition? document,
        Values.WorkflowTypeInference? inference, Values.WorkflowTypeInference? sharedInference, bool includePropertyOutputs)
    {
        descriptor = OscPorts.Resolve(descriptor, node);
        if (descriptor is not null && node is not null)
        {
            descriptor = Scripting.ScriptInputPorts.Resolve(descriptor, node);
            descriptor = SubworkflowPorts.Resolve(descriptor, node);
            if (includePropertyOutputs)
            {
                descriptor = PropertyPorts.Resolve(descriptor, node, document, inference ?? sharedInference?.WithDescriptor(descriptor));
            }
            if (document is not null && Targets.PcSelectionPorts.IsMerge(node, document, inference))
            {
                string valuePort = "value";
                descriptor = descriptor with
                {
                    Inputs = descriptor.Inputs.Where(port => 0 < ConcatPorts.Index(port.Name)).Select(port => port with { DataType = new(Targets.PcSelectionPorts.TypeName) }).ToArray(),
                    Outputs = descriptor.Outputs.Select(port =>
                    {
                        if (string.Equals(port.Name, valuePort, StringComparison.Ordinal))
                        {
                            return port with { ValueKind = EPortValueKind.PcSelection };
                        }
                        return port;
                    }).ToArray(),
                };
            }
        }
        descriptor = StartSignalPorts.WithSourceOutput(descriptor);
        descriptor = CompletionPorts.WithOutputs(descriptor);
        string typeNode = "value.type";
        string typeKey = "type";
        if (descriptor is null || node is null || !string.Equals(node.Type, typeNode, StringComparison.Ordinal)
            || node.Settings.ValueKind != JsonValueKind.Object || !node.Settings.TryGetProperty(typeKey, out JsonElement type) || type.ValueKind != JsonValueKind.String)
        {
            return descriptor;
        }
        string? input = type.GetString() switch { "array" => "elementType", "object" => "properties", _ => null };
        return descriptor with { Inputs = descriptor.Inputs.Where(port => string.Equals(port.Name, input, StringComparison.Ordinal)).ToArray() };
    }
}
