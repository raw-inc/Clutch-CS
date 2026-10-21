using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Values;

/// <summary>本文形式が受信まで未確定でも、個別ポートの候補スキーマは文書ごとに一度だけ解析します。</summary>
internal sealed class WorkflowPropertyTypes(IReadOnlyDictionary<string, NodeDefinition> nodes, WorkflowSettingValues settings)
{
    private readonly Dictionary<(string Node, string Port), ValueTypeDefinition?> known = [];

    /// <summary>候補なしも保持し、個別ポートごとの同じスキーマの解析を避けます。</summary>
    /// <returns>自動JSON本文の候補型、または該当なしです。</returns>
    internal ValueTypeDefinition? HttpJson(string nodeId, string port)
    {
        if (!known.TryGetValue((nodeId, port), out ValueTypeDefinition? type))
        {
            if (nodes.TryGetValue(nodeId, out NodeDefinition? node))
            {
                type = StructuredTypeSettings.AutomaticHttpJson(node, settings, port);
            }
            known[(nodeId, port)] = type;
        }
        return type;
    }
}
