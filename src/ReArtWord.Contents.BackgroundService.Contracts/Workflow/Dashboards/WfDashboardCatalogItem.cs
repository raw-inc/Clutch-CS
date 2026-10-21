namespace ReArtWord.Contents.BackgroundService.Contracts.Workflow.Dashboards;

/// <summary>入力値や通信内容を含めない、参照候補の表示情報です。</summary>
/// <param name="Reference">元WFとノードの物理参照です。</param>
/// <param name="WorkflowName">WFの表示名です。</param>
/// <param name="TypeName">ノード種別の表示名です。</param>
/// <param name="LogicalName">重複を許可する論理名です。</param>
public sealed record WfDashboardCatalogItem(WfDashboardReference Reference, string WorkflowName, string TypeName, string? LogicalName);
