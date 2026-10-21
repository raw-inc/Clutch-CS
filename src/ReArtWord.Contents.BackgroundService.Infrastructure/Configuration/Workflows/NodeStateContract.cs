using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;

/// <summary>自由値状態と内部の重複抑止マーカーの利用条件を区別します。</summary>
internal static class NodeStateContract
{
    /// <summary>描画位置・配線・無関係ノードの変更を契約変更とみなしません。</summary>
    /// <returns>既知の状態利用契約です。</returns>
    public static string Fingerprint(WorkflowDefinition definition)
    {
        string dedupe = "state.dedupe";
        string initial = ContractJson.Hash(new { schema = "node-state-v1", definition.Id,
            markers = definition.Nodes.Where(node => string.Equals(node.Type, dedupe, StringComparison.Ordinal))
                .OrderBy(node => node.Id, StringComparer.Ordinal).Select(node => new { node.Id, node.Settings, node.Inputs }) });
        string statePrefix = "state.";
        NodeDefinition[] changed = definition.Nodes.Where(node => node.Type.StartsWith(statePrefix, StringComparison.Ordinal) && NodeBehaviorContracts.Expected(node) != 1)
            .OrderBy(node => node.Id, StringComparer.Ordinal).ToArray();
        if (changed.Length == 0)
        {
            return initial;
        }
        return ContractJson.Hash(new { schema = "node-state-v2", initial, contracts = changed.Select(node => new { node.Id, node.Type, Version = NodeBehaviorContracts.Expected(node) }) });
    }
}
