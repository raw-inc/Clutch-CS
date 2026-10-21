using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;

namespace ReArtWord.Contents.BackgroundService.Contracts.Workflow;

/// <summary>一つの境界ノードが宣言する公開項目です。nullのNodeValueとデフォルト未指定は区別します。</summary>
public sealed record SubworkflowField(string NodeId, string Name, ValueTypeDefinition Type, NodeValue? DefaultValue);
