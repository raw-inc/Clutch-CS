using Microsoft.AspNetCore.Components;

namespace ReArtWord.Contents.BackgroundService.Client.Components;

/// <summary>画面で共有する識別名からSVGアイコンを選択します。</summary>
// 抑制理由: Razor が生成する public partial の公開範囲を維持します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor が生成する公開 partial 宣言と同じコンポーネント型を構成するため。")]
public sealed partial class UiIcon
{
    /// <summary>表示するアイコンの識別名です。</summary>
    [Parameter]
    public string Name
    {
        get;
        set;
    } = string.Empty;
}
