namespace ReArtWord.Contents.BackgroundService.Contracts.Workflow.Dashboards;

/// <summary>元ノードの型と配線を変更せず、確認した版のValueだけを変更します。</summary>
/// <param name="OperationId">再送・結果照会で同じ保存を識別する操作IDです。</param>
/// <param name="Reference">変更先のValueです。</param>
/// <param name="Revision">入力開始時に確認した元WFの保存版です。</param>
/// <param name="Text">固定型で解釈する入力文字列です。</param>
public sealed record WfDashboardValueRequest(Guid OperationId, WfDashboardReference Reference, string Revision, string Text);
