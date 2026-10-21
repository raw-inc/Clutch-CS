namespace ReArtWord.Contents.BackgroundService.Contracts.Workflow;

/// <summary>ノードの実行設定と分離したエディター上の寸法です。</summary>
/// <param name="Width">キャンバス座標での横幅です。</param>
/// <param name="Height">キャンバス座標での高さです。</param>
public sealed record NodeSize(double Width, double Height);
