namespace ReArtWord.Contents.BackgroundService.Contracts.Workflow.Dashboards;

/// <summary>検索条件に一致した有限の候補です。</summary>
/// <param name="Items">表示する候補です。</param>
/// <param name="NextOffset">次ページの位置、または最終ページのnullです。</param>
public sealed record WfDashboardCatalogPage(IReadOnlyList<WfDashboardCatalogItem> Items, int? NextOffset);
