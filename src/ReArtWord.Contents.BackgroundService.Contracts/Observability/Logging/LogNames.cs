namespace ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;

/// <summary>現在の保存定義から確認した表示名です。削除済みの対象はnullまたは欠落で示します。</summary>
/// <param name="WorkflowName">WFの論理名です。</param>
/// <param name="Revision">名前を取得した保存版です。</param>
/// <param name="Nodes">論理名があるノードだけのIDと名前です。</param>
public sealed record LogNames(string? WorkflowName, string? Revision, IReadOnlyDictionary<string, string> Nodes);
