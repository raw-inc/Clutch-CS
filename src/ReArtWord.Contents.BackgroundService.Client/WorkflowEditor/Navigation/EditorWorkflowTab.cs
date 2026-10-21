namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Navigation;

/// <summary>本文を複製せずtabの識別と草稿状態だけを保持します。</summary>
/// <param name="Id">元ワークフロー IDです。</param>
/// <param name="Name">表示名です。</param>
/// <param name="Dirty">最後に開いた時点の未保存草稿の有無です。</param>
/// <param name="LocalOnly">Peerローカル保存版かです。</param>
/// <param name="IsSubworkflow">公開境界を持つ子Workflowかです。</param>
/// <param name="IsDashboard">実行を持たないWFDashboardかです。</param>
internal sealed record EditorWorkflowTab(string Id, string Name, bool Dirty, bool LocalOnly, bool IsSubworkflow = false, bool IsDashboard = false);
