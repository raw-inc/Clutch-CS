using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Client.Components;

/// <summary>実行に使用したPC候補、リソース観測、比較できなかった理由を表示します。</summary>
// 抑制理由: Razorが生成する公開宣言に合わせます。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開宣言と同じ型を構成するため。")]
public sealed partial class PcSelectionDetails
{
    /// <summary>選択時の観測を含む、元の実行結果。</summary>
    [Parameter]
    public ExecutionReport? Report
    {
        get; set;
    }
}
