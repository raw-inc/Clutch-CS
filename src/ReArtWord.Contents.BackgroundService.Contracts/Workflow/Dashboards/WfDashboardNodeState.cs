namespace ReArtWord.Contents.BackgroundService.Contracts.Workflow.Dashboards;

/// <summary>元WFの定義全体・配線・ボタンの出力値を含めないカード情報です。</summary>
/// <param name="Reference">表示対象の物理参照です。</param>
/// <param name="WorkflowName">元WFの表示名です。</param>
/// <param name="Title">論理名、未設定なら種類の表示名です。</param>
/// <param name="Revision">確認した保存版です。</param>
/// <param name="Active">同じ版を実行できるかです。</param>
/// <param name="Label">ボタンに設定された表示文字列です。</param>
/// <param name="ValueText">固定型で編集するValueの表記です。</param>
/// <param name="ValueType">Valueの確認済み型です。型変更には使用しません。</param>
/// <param name="Problem">参照・表示・編集ができない理由です。</param>
public sealed record WfDashboardNodeState(WfDashboardReference Reference, string WorkflowName, string Title,
    string? Revision, bool Active, string? Label = null, string? ValueText = null, string? ValueType = null, string? Problem = null)
{
    /// <summary>制限ノードだけが持つ、入力本文や履歴を含まない現在の待機一覧です。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Execution.Concurrency.WorkflowConcurrencySnapshot? Concurrency
    {
        get; init;
    }
}
