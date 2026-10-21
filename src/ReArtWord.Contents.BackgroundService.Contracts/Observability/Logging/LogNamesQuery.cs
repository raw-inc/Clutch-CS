namespace ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;

/// <summary>ログ表示に必要な名前だけを取得し、スクリプトやWF定義本文を転送しません。</summary>
/// <param name="WorkflowId">保存定義の識別子です。</param>
/// <param name="NodeIds">名前が必要な最大256個のノードです。</param>
public sealed record LogNamesQuery(string WorkflowId, IReadOnlyList<string> NodeIds);
