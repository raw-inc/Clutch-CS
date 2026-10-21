using System.Diagnostics.CodeAnalysis;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Catalog;

/// <summary>選んだ型と、graph座標へ変換する前の画面位置です。</summary>
/// <param name="Descriptor">追加するノードの型です。</param>
/// <param name="ClientX">ブラウザー表示領域の横座標です。</param>
/// <param name="ClientY">ブラウザー表示領域の縦座標です。</param>
// 抑制理由: Razor生成コンポーネントのpublic EventCallback引数に必要な公開型です。
[SuppressMessage("Design", "CA1515", Justification = "Razor生成componentのpublic parameterと同じアクセス範囲が必要です。")]
public sealed record NodePlacementRequest(NodeDescriptor Descriptor, double ClientX, double ClientY);
