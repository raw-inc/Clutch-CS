using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Fleet;

namespace ReArtWord.Contents.BackgroundService.Client.Overview.Updates;

/// <summary>全画面のフッターへ、更新中かどうかと小さな進捗だけを表示します。</summary>
// 抑制理由: Razorの公開partial宣言です。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorの公開partial宣言です。")]
public sealed partial class FleetUpdateFooter
{
    /// <summary>所有者を問わない、操作IDで統合した進捗です。</summary>
    // 抑制理由: 親が取得した不変の一覧を受け取ります。
    [Parameter, System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "親が取得した不変の一覧を受け取ります。")]
    public IReadOnlyList<FleetUpdateActivity> Updates
    {
        get; set;
    } = [];

    /// <summary>通知が途切れている操作です。</summary>
    // 抑制理由: 親が取得した不変の一覧を受け取ります。
    [Parameter, System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "親が取得した不変の一覧を受け取ります。")]
    public IReadOnlyList<Guid> StaleOperations
    {
        get; set;
    } = [];

    /// <summary>最新照会の確認不能です。</summary>
    [Parameter]
    public bool Unavailable
    {
        get; set;
    }
}
