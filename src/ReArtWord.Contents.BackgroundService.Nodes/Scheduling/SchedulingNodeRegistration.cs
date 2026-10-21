using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Scheduling;

/// <summary>時間指定の契機ノードを登録します。</summary>
public static class SchedulingNodeRegistration
{
    /// <summary>契機を待つノードと、現在時刻を取得するノードを列挙します。</summary>
    /// <returns>登録する時間指定ノードです。</returns>
    public static IReadOnlyList<INode> Create()
    {
        return [new ScheduleNode(), new CurrentTimeNode()];
    }
}
