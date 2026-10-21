using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;

/// <summary>クリックとドラッグの接続検査を、コンパイラと同じ型契約へ渡します。</summary>
internal static class GraphConnectionPolicy
{
    /// <summary>値を実行せず、共通の推測で確定した不適合だけを拒否します。</summary>
    /// <returns>型契約上の接続可否です。</returns>
    internal static bool CanConnect(WorkflowDefinition document, IReadOnlyList<NodeDescriptor> catalog, WireDefinition wire)
        => WorkflowConnectionTypes.CanReplaceConnection(document, catalog, wire);
}
