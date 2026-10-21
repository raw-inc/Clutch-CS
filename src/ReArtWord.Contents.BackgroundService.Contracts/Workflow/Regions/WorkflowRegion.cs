namespace ReArtWord.Contents.BackgroundService.Contracts.Workflow.Regions;

/// <summary>開始・任意の終了と、それらが所有するグラフ上の実行区間です。</summary>
/// <param name="Start">区間開始ノード。</param>
/// <param name="End">省略可能な終了ノード。</param>
/// <param name="Members">開始・ボディ・終了を含むノード集合。</param>
public sealed record WorkflowRegion(NodeDefinition Start, NodeDefinition? End, IReadOnlySet<string> Members);
