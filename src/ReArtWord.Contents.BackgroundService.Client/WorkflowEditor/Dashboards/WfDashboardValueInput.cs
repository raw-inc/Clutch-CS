namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Dashboards;

/// <summary>入力開始時の版を保持し、後着の再描画で比較元を進めません。</summary>
/// <param name="Revision">入力開始時の保存版です。</param>
/// <param name="Text">固定型で保存する値です。</param>
// 抑制理由: 公開Razorコンポーネントのイベント引数型です。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "公開Razorコンポーネントのイベント引数型です。")]
public sealed record WfDashboardValueInput(string Revision, string Text);
