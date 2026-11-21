using Microsoft.AspNetCore.Components;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;

/// <summary>接続で決まる値をノード内に表示します。</summary>
// 抑制理由: Razorが生成する公開partial宣言と同じコンポーネント型を構成します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開partial宣言と同じ型を構成するため。")]
public sealed partial class NodeReadout
{
    /// <summary>入力元の値、または値を取得できない理由です。</summary>
    [Parameter]
    public string? Text
    {
        get;
        set;
    }
}
