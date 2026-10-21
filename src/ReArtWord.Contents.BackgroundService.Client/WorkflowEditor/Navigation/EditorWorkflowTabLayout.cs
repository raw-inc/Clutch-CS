namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Navigation;

/// <summary>WF本文を複製しないブラウザー内のタブ設定です。</summary>
/// <param name="Order">取得済みタブの表示順です。</param>
/// <param name="Hidden">一覧には残す非表示タブです。</param>
/// <param name="Tabs">未保存草稿やページ外のWFへ戻るための表示情報です。</param>
internal sealed record EditorWorkflowTabLayout(IReadOnlyList<string> Order, IReadOnlyList<string> Hidden, IReadOnlyList<EditorWorkflowTab> Tabs);
