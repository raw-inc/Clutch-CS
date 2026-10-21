using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Media;

/// <summary>媒体ごとの変換ノードを登録します。</summary>
public static class MediaNodeRegistration
{
    /// <summary>共通の許可範囲と実行物から変換ノードを作ります。</summary>
    /// <returns>音声、動画、静止画変換ノードです。</returns>
    public static IReadOnlyList<INode> Create(MediaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return [new MediaConverterNode(options, "audio"), new MediaConverterNode(options, "video"), new MediaConverterNode(options, "picture")];
    }
}
