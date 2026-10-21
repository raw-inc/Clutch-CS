using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;

/// <summary>開いている文書一版の解析とプレビューだけを所有し、切替時に旧版を解放します。</summary>
internal sealed class EditorPresentationCache
{
    private GraphTypeInference? graph;
    private ConnectedValuePreview? preview;

    /// <summary>同じ文書とカタログの解析結果をキャンバスと詳細欄で共有します。</summary>
    /// <returns>現在版だけの表示用解析です。</returns>
    internal GraphTypeInference Graph(WorkflowDefinition document, IReadOnlyList<NodeDescriptor> catalog)
    {
        if (graph is null || !graph.Matches(document, catalog))
        {
            graph = new(document, catalog);
        }
        return graph;
    }

    /// <summary>保存値か未確定入力が変わるまで、副作用のないプレビューを共有します。</summary>
    /// <returns>現在の入力に対応するプレビューです。</returns>
    internal ConnectedValuePreview Preview(EditorDraft draft)
    {
        if (preview is null || !preview.Matches(draft))
        {
            preview = new(draft);
        }
        return preview;
    }
}
