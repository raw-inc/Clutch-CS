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

    private string Path => Name switch
    {
        "dashboard" => "M3 3h7v7H3z M14 3h7v7h-7z M3 14h7v7H3z M14 14h7v7h-7z",
        "monitor" => "M3 4h18v13H3z M8 21h8 M12 17v4",
        "values" => "M7 4H5v16h2 M17 4h2v16h-2 M9 8h6 M12 8v8 M10 16h4",
        "subworkflow" => "M3 3h18v18H3z M7 7h4v4H7z M13 13h4v4h-4z M9 11v4h4",
        "nodes" => "M3 3h6v6H3z M15 15h6v6h-6z M15 3h6v6h-6z M9 6h6 M6 9v9h9",
        "logs" => "M5 3h14v18H5z M9 8h6 M9 12h6 M9 16h4",
        "terminal" => "M3 4h18v16H3z M7 9l3 3-3 3 M13 15h4",
        "folder" => "M3 7V4h6l2 3h10v13H3z M8 12h8 M13 9l3 3-3 3",
        "file" => "M6 3h8l4 4v14H6z M14 3v5h4",
        "drive" => "M5 4h14l3 12v4H2v-4z M2 16h20 M6 18h1 M10 18h1",
        "add" => "M12 5v14 M5 12h14",
        "search" => "M20 20l-5-5 M17 10a7 7 0 1 1-14 0 7 7 0 0 1 14 0",
        "play" => "M8 5l11 7-11 7z",
        "check" => "M5 12l4 4L19 6",
        "save" => "M4 3h13l3 3v15H4z M8 3v6h8V3 M8 21v-8h8v8",
        "undo" => "M9 5l-5 5 5 5 M4 10h10a6 6 0 0 1 0 12",
        "chevron" => "M7 10l5 5 5-5",
        "trash" => "M4 6h16 M9 6V3h6v3 M6 6l1 15h10l1-15 M10 10v7 M14 10v7",
        "close" => "M6 6l12 12 M18 6 6 18",
        "panel" => "M3 4h18v16H3z M15 4v16",
        "refresh" => "M20 7v5h-5 M4 17v-5h5 M6 7a7 7 0 0 1 12-1l2 3 M4 15l2 3a7 7 0 0 0 12-1",
        "settings" => "M4 7h16 M4 17h16 M8 4v6 M16 14v6",
        "signal" => "M2 12h4l3-8 6 16 3-8h4",
        "more" => "M5 11v2 M12 11v2 M19 11v2",
        _ => "M5 12h14"
    };
}
