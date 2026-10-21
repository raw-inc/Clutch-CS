using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;

namespace ReArtWord.Contents.BackgroundService.Contracts.Workflow.Dashboards;

/// <summary>型固定の変更を、通常の保存APIへ同じ操作IDで渡す準備結果です。</summary>
/// <param name="Request">確認した元WFの版を条件とする保存要求です。</param>
/// <param name="Issues">書込み前に検出した変更不能の理由です。</param>
public sealed record WfDashboardValuePreparation(WorkflowSaveRequest? Request, IReadOnlyList<ValidationIssue> Issues);
