using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Contracts.Execution;

/// <summary>同じ操作IDで暗黙の二重実行を起こさない手動実行要求です。</summary>
/// <param name="OperationId">利用者が継続照会する操作識別子。</param>
/// <param name="WorkflowId">実行する定義。</param>
/// <param name="Revision">想定する有効版。省略時は受付時に固定します。</param>
/// <param name="EntryNodeId">明示入口。省略時は有限の根から開始します。</param>
/// <param name="Inputs">入口へ渡す入力。</param>
public sealed record WorkflowRunRequest(Guid OperationId, string WorkflowId, string? Revision = null, string? EntryNodeId = null, IReadOnlyDictionary<string, NodeValue>? Inputs = null);
