using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Runtime.Nodes;

/// <summary>外部副作用・待機・保存を行わず、設定と入力だけから値を構築します。</summary>
public interface IValueNode : IRuntimeValueNode
{
    /// <summary>通常実行と設定用DAGで同じ純粋評価を使用します。</summary>
    /// <returns>構築した値または明示的な入力失敗です。</returns>
    public NodeResult Evaluate(NodeDefinition definition, IReadOnlyDictionary<string, NodeValue> inputs);
}
