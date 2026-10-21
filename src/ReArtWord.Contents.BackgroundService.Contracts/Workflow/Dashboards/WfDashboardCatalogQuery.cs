namespace ReArtWord.Contents.BackgroundService.Contracts.Workflow.Dashboards;

/// <summary>参照候補の本文を運ばず、名前と物理IDを検索します。</summary>
/// <param name="Search">論理名、物理ID、種類、WF名の検索語です。</param>
/// <param name="Offset">表示済み候補の位置です。</param>
/// <param name="Limit">一回に表示する件数です。</param>
public sealed record WfDashboardCatalogQuery(string Search = "", int Offset = 0, int Limit = 100);
