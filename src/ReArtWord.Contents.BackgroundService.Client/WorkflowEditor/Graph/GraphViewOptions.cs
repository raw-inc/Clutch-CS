using System.Diagnostics.CodeAnalysis;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;

/// <summary>ワークフローとは独立した表示だけの選択です。</summary>
/// <param name="WireShape">接続線の形状です。</param>
/// <param name="AnimateConnections">実行状態ではない方向ガイドを動かします。</param>
/// <param name="ShowPortTypes">宣言型と共通ポートの役割を表示します。</param>
/// <param name="HighlightRunning">保存版の処理中ノードを発光します。未設定時は有効です。</param>
/// <param name="RealtimePointer">操作時共有から移動中の座標共有へ切り替えます。</param>
// 抑制理由: Razorが生成するpublic コンポーネントの公開Parameter型に必要です。
[SuppressMessage("Performance", "CA1515", Justification = "Razorの公開Parameter契約として使用します。") ]
public sealed record GraphViewOptions(EGraphWireShape WireShape = EGraphWireShape.Curve, bool AnimateConnections = true, bool ShowPortTypes = true, bool HighlightRunning = true, bool RealtimePointer = false)
{
    /// <summary>保存した選択を維持したまま、試験中のポインター共有だけをURL指定で制限します。</summary>
    /// <returns>今回の画面で使用する表示設定です。</returns>
    internal GraphViewOptions ForEditor(bool experimental) => this with { RealtimePointer = experimental && RealtimePointer };
}
