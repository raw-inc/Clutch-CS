using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.InternalData;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow.Dashboards;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Dashboards;

/// <summary>WF本文やState一覧を取り込まず、表示対象の範囲とKeyを指定します。</summary>
// 抑制理由: Razorが生成する公開partialと一致させます。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開partialと一致させます。")]
public sealed partial class WfDashboardStatePicker
{
    /// <summary>表示カードを追加する操作です。</summary>
    [Parameter]
    public EventCallback<WfDashboardStateReference> Added
    {
        get; set;
    }

    private readonly List<WorkflowListSummary> workflows = [];
    private string workflow = string.Empty, key = string.Empty;
    private EInternalDataScope scope;
    private string? cursor, problem;
    private bool busy;
    private InternalDataTarget Target => new(scope == EInternalDataScope.Cluster ? string.Empty
                                                                                 : workflow, scope, key);

    /// <summary>表示時だけWF名の軽量一覧を取得します。</summary>
    /// <returns>最初の候補一覧です。</returns>
    // 抑制理由: Blazorの固定署名で画面寿命のトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "Blazorの固定署名です。")]
    protected override Task OnInitializedAsync() => LoadAsync(UiCancellation);

    /// <summary>未取得のWFだけをページとして読み、定期取得はしません。</summary>
    /// <returns>候補の取得です。</returns>
    private async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (busy)
        {
            return;
        }
        busy = true;
        try
        {
            WorkflowListPage? page = await Api.PostAsync<WorkflowListPage>("/api/workflows/query", new WorkflowListQuery(Cursor: cursor), cancellationToken).ConfigureAwait(true);
            if (page is null)
            {
                throw new InvalidOperationException("Workflow list unavailable");
            }
            if (cursor is null)
            {
                workflows.Clear();
            }
            workflows.AddRange(page.Entries.Where(item => item.Id is not null && !item.IsDashboard));
            cursor = page.Cursor;
            problem = null;
        }
        catch (Exception error) when (error is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException or OperationCanceledException)
        {
            problem = "The workflow list could not be confirmed.";
        }
        finally
        {
            busy = false;
        }
    }
}
