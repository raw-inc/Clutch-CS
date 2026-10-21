using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Nodes;

/// <summary>公開済み定義と新規草稿に同じカスタムレシピ契約を与えます。</summary>
public static class SubworkflowRecipeDescriptor
{
    /// <summary>公開境界を配置時のポートへ変換します。</summary>
    /// <returns>内部で展開するグループの編集用宣言です。</returns>
    public static NodeDescriptor Create(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        SubworkflowInterface contract = SubworkflowInterface.Read(definition);
        return new(SubworkflowInterface.CallType(definition.Id), definition.Name, "SubWorkflows", "Reuse this node group in the containing workflow.",
            ENodeKind.Action, EExecutionLocation.Independent, contract.Inputs.Select(field => SubworkflowInterface.Port(field, true)).ToArray(),
            contract.Outputs.Select(field => SubworkflowInterface.Port(field, false)).ToArray(), ["success"],
            JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"$subworkflowDefinition":{"type":"object","x-runtime-metadata":true,"x-connectable":false}}}"""));
    }
}
