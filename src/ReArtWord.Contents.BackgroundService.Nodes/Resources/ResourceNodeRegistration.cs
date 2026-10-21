using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Resources;

namespace ReArtWord.Contents.BackgroundService.Nodes.Resources;

/// <summary>同じ共通リソース収集へ接続する有限・継続ノードを登録します。</summary>
public static class ResourceNodeRegistration
{
    /// <summary>Host共通観測サービスからノード一覧を作ります。</summary>
    /// <returns>現在値取得と継続監視ノードです。</returns>
    public static IReadOnlyList<INode> Create(IResourceService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        return [new ResourceObserveNode(service), new ResourceWatchNode(service)];
    }
}
