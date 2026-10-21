using ReArtWord.Contents.BackgroundService.Nodes.Control.Iteration;
using ReArtWord.Contents.BackgroundService.Nodes.Control.Subworkflows;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;

namespace ReArtWord.Contents.BackgroundService.Nodes.Control;

/// <summary>条件と通常の制御ノードを列挙します。</summary>
public static class ControlNodeRegistration
{
    /// <summary>共通の事象・ログ境界を注入してノードを作成します。</summary>
    /// <returns>登録するノードです。</returns>
    public static IReadOnlyList<INode> Create(IWorkflowEvents events, IWorkflowLog log)
    {
        return [new SubworkflowBoundaryNode(false), new SubworkflowBoundaryNode(true), new ButtonNode(), new ValueMergeNode(), new ConditionNode(), new IfNode(), new IfNode(inverted: true), new IfElseNode(), new EventHookNode(events), new SignalWatchdogNode(), new PassNode(), new DelayNode(), new PublishEventNode(events), new WriteLogNode(log), new ForEachNode(), new ForEachNode(parallel: true), new ForEachEndNode(), new ConcurrencyLimitNode(), new ConcurrencyLimitNode(end: true)];
    }
}
