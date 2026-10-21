using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Iteration;

/// <summary>コンパイル時に固定した一つの反復範囲です。</summary>
/// <param name="Start">反復開始。</param>
/// <param name="End">省略可能な収集境界。</param>
/// <param name="Members">開始、ボディ、終了を含む所有範囲。</param>
internal sealed record IterationRegion(NodeDefinition Start, NodeDefinition? End, HashSet<string> Members);
