using System.Diagnostics.CodeAnalysis;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;

/// <summary>実行意味を変えない接続線の表示形状です。</summary>
// 抑制理由: Razorが生成するpublic コンポーネントの公開Parameter型に必要です。
[SuppressMessage("Performance", "CA1515", Justification = "Razorの公開Parameter契約として使用します。") ]
public enum EGraphWireShape
{
    /// <summary>滑らかな曲線です。</summary>
    Curve,

    /// <summary>端点を結ぶ直線です。</summary>
    Straight,

    /// <summary>直角で折り返す線です。</summary>
    Orthogonal,
}
