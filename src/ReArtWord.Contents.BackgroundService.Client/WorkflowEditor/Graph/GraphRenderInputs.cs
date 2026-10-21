using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;

/// <summary>親の定期照会と、キャンバスの内容が変わる更新を区別します。</summary>
internal sealed record GraphRenderInputs(WorkflowDefinition Document, object? InputDrafts, GraphTypeInference Inference,
    string? Cluster, string? SelectedNode, int? SelectedWire, GraphViewOptions View, double Zoom, bool Details, string Language,
    string Locks, string Diagnostics, int PreviewVersion, int Generation, int ChoicesVersion, int ValueVersion, int SelectionVersion, string TimeZone, int ConcurrencyVersion)
{
    /// <summary>文書本文は不変参照を使い、実行結果や復元保護の変更も取りこぼしません。</summary>
    /// <returns>一回の親更新で確認する描画条件です。</returns>
    internal static GraphRenderInputs Read(EditorDocument model, GraphTypeInference inference, GraphViewOptions view, double zoom, bool details,
        string language, EditorDiagnostics diagnostics, EditorPreviews previews, int choicesVersion = 0, string timeZone = "UTC", int concurrencyVersion = 0)
        => new(model.Draft.Document, model.Draft.InputDrafts, inference, model.ClusterId, model.SelectedNode, model.SelectedWire, view, zoom, details, language,
            ContractJson.Hash(model.Locks.Current), ContractJson.Hash(diagnostics.Issues(model.ClusterId, model.Draft.Document.Id)), previews.Version, model.Generation, choicesVersion, diagnostics.Values.VersionFor(model.ClusterId, model.Draft), model.Selection.Version, timeZone, concurrencyVersion);
}
