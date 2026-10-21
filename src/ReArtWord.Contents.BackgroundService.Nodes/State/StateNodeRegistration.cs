using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.State;

namespace ReArtWord.Contents.BackgroundService.Nodes.State;

/// <summary>汎用状態の操作を列挙します。</summary>
public static class StateNodeRegistration
{
    /// <summary>同じ保存境界を共有するノードを登録します。</summary>
    /// <returns>状態処理ノードです。</returns>
    public static IReadOnlyList<INode> Create(INodeStateStore store)
    {
        return [new StateNode("get", store), new StateNode("set", store), new StateNode("compareExchange", store), new StateNode("delete", store), new StateNode("dedupe", store), new StateChangedNode(store)];
    }
}
