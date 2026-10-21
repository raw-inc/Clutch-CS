using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Contracts.State;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow.Dashboards;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Dashboards;

/// <summary>Stateの存在・値・通信状態を表示専用カードへ反映します。</summary>
// 抑制理由: Razorが生成する公開partialと一致させます。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開partialと一致させます。")]
public sealed partial class WfDashboardStateCard
{
    /// <summary>表示するKeyと永続保存範囲です。</summary>
    [Parameter, EditorRequired]
    public WfDashboardStateReference Reference
    {
        get; set;
    } = null!;

    /// <summary>変更通知で届いた表示値です。</summary>
    [Parameter]
    public StateStreamView? State
    {
        get; set;
    }

    /// <summary>切断時にも直近値と区別して表示する問題です。</summary>
    [Parameter]
    public string? Problem
    {
        get; set;
    }

}
