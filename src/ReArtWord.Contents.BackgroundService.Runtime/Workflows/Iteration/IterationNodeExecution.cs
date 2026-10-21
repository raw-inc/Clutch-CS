using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Iteration;

/// <summary>一要素の入力範囲と後続停止の状態を、通常のノード実行へ渡します。</summary>
/// <returns>開始済みの処理を確認した実結果です。</returns>
internal delegate Task<ExecutionFrame> IterationNodeExecution(NodeDefinition node, Task<ExecutionFrame>[] parents, bool entry,
    IReadOnlyDictionary<string, NodeValue>? inputs, Func<string, Task<ExecutionFrame>?> read, Guid iterationScope, Func<bool>? stopped);
