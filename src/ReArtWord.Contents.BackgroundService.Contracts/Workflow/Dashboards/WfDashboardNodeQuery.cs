namespace ReArtWord.Contents.BackgroundService.Contracts.Workflow.Dashboards;

/// <summary>画面に配置したカードだけを、同じクラスタから取得します。</summary>
/// <param name="References">最大64件の物理参照です。</param>
public sealed record WfDashboardNodeQuery(IReadOnlyList<WfDashboardReference> References);
