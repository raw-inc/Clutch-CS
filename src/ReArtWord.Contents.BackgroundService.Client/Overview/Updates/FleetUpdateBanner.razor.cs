using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Fleet;

namespace ReArtWord.Contents.BackgroundService.Client.Overview.Updates;

/// <summary>全Dashboardタブの上部で固定計画の確認済み進捗を表示します。</summary>
// 抑制理由: Razorの公開partial宣言と同じ公開範囲です。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorの公開partial宣言です。")]
public sealed partial class FleetUpdateBanner
{
    /// <summary>Dashboardが取得した一覧です。</summary>
    // 抑制理由: 親が取得したスナップショットをParameterとして受け取り、内容を変更しません。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "RazorのParameterとして親のスナップショットを受け取ります。")]
    [Parameter] public IReadOnlyList<FleetUpdateActivity> Updates
    {
        get; set;
    } = [];

    /// <summary>このClientHostから詳細を確認できる元操作です。</summary>
    // 抑制理由: 親が取得した不変の一覧を受け取ります。
    [Parameter, System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "親が取得した不変の一覧を受け取ります。")]
    public IReadOnlyList<Guid> OwnedOperations
    {
        get; set;
    } = [];

    /// <summary>通知停止を個々の操作に表示します。</summary>
    // 抑制理由: 親が取得した不変の一覧を受け取ります。
    [Parameter, System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "親が取得した不変の一覧を受け取ります。")]
    public IReadOnlyList<Guid> StaleOperations
    {
        get; set;
    } = [];

    /// <summary>前回の取得値を保持しているかです。</summary>
    [Parameter] public bool Unavailable
    {
        get; set;
    }

    /// <summary>同じ更新の詳細パネルを表示します。</summary>
    [Parameter] public EventCallback ShowDetails
    {
        get; set;
    }
}
