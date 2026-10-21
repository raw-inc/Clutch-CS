using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Values;

namespace ReArtWord.Contents.BackgroundService.Contracts.Workflow;

/// <summary>境界ノードからクラスタ内呼出しの公開引数と返却値を読み取ります。</summary>
public sealed record SubworkflowInterface(IReadOnlyList<SubworkflowField> Inputs, IReadOnlyList<SubworkflowField> Outputs)
{
    /// <summary>呼出し引数を受け取る境界です。</summary>
    public static string InputType => "subworkflow.input";

    /// <summary>呼出し元へ返す境界です。</summary>
    public static string OutputType => "subworkflow.output";

    /// <summary>展開済みの公開境界を中継する内部ノードです。</summary>
    public static string PortType => "subworkflow.port";

    /// <summary>呼出しレシピの種類識別子です。</summary>
    public static string CallPrefix => "subworkflow.call:";

    /// <summary>呼出し時だけ保持する不変の子定義です。</summary>
    public static string CapturedDefinition => "$subworkflowDefinition";

    /// <summary>入出力どちらか一つでも置かれたらSubWorkflowとして扱います。</summary>
    /// <returns>境界ノードがあるかです。</returns>
    public static bool IsSubworkflow(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.IsRecipe || definition.Nodes.Any(node => IsBoundary(node.Type));
    }

    /// <summary>表示・検証・実行で境界の判定を共有します。</summary>
    /// <returns>公開入出力を表す種類かです。</returns>
    public static bool IsBoundary(string type) => string.Equals(type, InputType, StringComparison.Ordinal) || string.Equals(type, OutputType, StringComparison.Ordinal);

    /// <summary>同じ名前の入力と出力は許容し、同方向での重複を拒否します。</summary>
    /// <returns>固定された公開契約です。</returns>
    public static SubworkflowInterface Read(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        SubworkflowField[] inputs = definition.Nodes.Where(node => string.Equals(node.Type, InputType, StringComparison.Ordinal)).Select(ReadField).ToArray();
        SubworkflowField[] outputs = definition.Nodes.Where(node => string.Equals(node.Type, OutputType, StringComparison.Ordinal)).Select(ReadField).ToArray();
        if (inputs.Length + outputs.Length == 0 && !definition.IsRecipe || 128 < inputs.Length || 128 < outputs.Length
            || inputs.DistinctBy(field => field.Name, StringComparer.Ordinal).Count() != inputs.Length
            || outputs.DistinctBy(field => field.Name, StringComparer.Ordinal).Count() != outputs.Length)
        {
            throw new FormatException("subworkflow.interface.invalid");
        }
        return new(inputs, outputs);
    }

    /// <summary>名前・型と、明示したデフォルトの有無を分離して保持します。</summary>
    /// <returns>一つの公開項目です。</returns>
    public static SubworkflowField ReadField(NodeDefinition node)
    {
        ArgumentNullException.ThrowIfNull(node);
        string nameKey = "name", typeKey = "valueType", defaultKey = "defaultValue", enabledKey = "hasDefault";
        if (node.Settings.ValueKind != JsonValueKind.Object || !node.Settings.TryGetProperty(nameKey, out JsonElement nameValue)
            || nameValue.ValueKind != JsonValueKind.String || !node.Settings.TryGetProperty(typeKey, out JsonElement typeValue))
        {
            throw new FormatException("subworkflow.field.required");
        }
        string name = nameValue.GetString()!;
        string[] reserved = ["trigger", "result", "source", "execution", "fileTransaction"];
        if (string.IsNullOrWhiteSpace(name) || 128 < name.Length || name.Contains('/', StringComparison.Ordinal) || name.StartsWith('$')
            || reserved.Contains(name, StringComparer.Ordinal))
        {
            throw new FormatException("subworkflow.field.name");
        }
        ValueTypeDefinition type = typeValue.Deserialize<ValueTypeDefinition>(ContractJson.Options) ?? throw new FormatException("subworkflow.field.type");
        NodeValue? fallback = null;
        bool hasDefault = node.Settings.TryGetProperty(enabledKey, out JsonElement enabled) && enabled.ValueKind == JsonValueKind.True;
        if (hasDefault)
        {
            if (!node.Settings.TryGetProperty(defaultKey, out JsonElement value))
            {
                throw new FormatException("subworkflow.default.required");
            }
            fallback = NodeValue.FromJson(value);
        }
        return new(node.Id, name, type, fallback);
    }

    /// <summary>任意のワークフロー識別子を種類名として可逆に保持します。</summary>
    /// <returns>クラスタ内のレシピ種類です。</returns>
    public static string CallType(string workflowId) => CallPrefix + Uri.EscapeDataString(workflowId);

    /// <summary>組込み境界と呼出しレシピを取り違えません。</summary>
    /// <returns>参照するワークフロー識別子です。</returns>
    public static string? Target(string type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.StartsWith(CallPrefix, StringComparison.Ordinal) ? Uri.UnescapeDataString(type[CallPrefix.Length..])
                                                                     : null;
    }

    /// <summary>子実行へ固定した定義がある場合だけ、現在のカタログより優先します。</summary>
    /// <returns>呼出し中に固定した定義、または未固定です。</returns>
    public static WorkflowDefinition? Captured(NodeDefinition node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.Settings.ValueKind == JsonValueKind.Object && node.Settings.TryGetProperty(CapturedDefinition, out JsonElement value))
        {
            return value.Deserialize<WorkflowDefinition>(ContractJson.Options);
        }
        return null;
    }

    /// <summary>共通のポート種別と詳細型を宣言します。</summary>
    /// <returns>デフォルトなしの入力は必須です。</returns>
    public static PortDescriptor Port(SubworkflowField field, bool input)
    {
        ArgumentNullException.ThrowIfNull(field);
        EPortValueKind kind = field.Type.Type switch { "binary" => EPortValueKind.Binary, "pc-selection" => EPortValueKind.PcSelection, _ => EPortValueKind.Any };
        return new(field.Name, kind, input && field.DefaultValue is null) { DataType = field.Type };
    }
}
