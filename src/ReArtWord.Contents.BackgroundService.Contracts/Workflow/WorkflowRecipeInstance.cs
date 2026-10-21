namespace ReArtWord.Contents.BackgroundService.Contracts.Workflow;

/// <summary>一つの配置に対応する読み取り専用の内部レイアウトです。</summary>
/// <param name="NodeId">配置元ノード。入れ子では配置ごとの識別子です。</param>
/// <param name="DefinitionId">再利用した定義の識別子です。</param>
/// <param name="Layout">ノードIDと配線を実行側の識別子へ対応させた表示です。</param>
public sealed record WorkflowRecipeInstance(string NodeId, string DefinitionId, WorkflowDefinition Layout);
