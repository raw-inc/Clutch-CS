using System.Text.Json;
using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.Components;

/// <summary>保存済みワークフローを件数ごとに分けて取得して選択します。</summary>
// 抑制理由: Razor が生成する public partial の公開範囲を維持します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor が生成する公開 partial 宣言と同じコンポーネント型を構成するため。")]
public sealed partial class WorkflowPagePicker
{
    [Inject]
    private ManagementApi Api
    {
        get;
        set;
    } = null!;

    /// <summary>選択した対象を親画面へ通知します。</summary>
    [Parameter]
    public EventCallback<WorkflowListSummary> Selected
    {
        get;
        set;
    }

    // 抑制理由: 取得したスナップショットの参照を交換し、子画面や開始済み処理が保持する旧コレクションを変更しないため。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "取得したスナップショットの参照を交換し、子画面や開始済み処理が保持する旧コレクションを変更しないため。")]
    private IReadOnlyList<WorkflowListSummary> entries = [];
    private string? cursor;
    private Func<string>? message;
    private bool busy;

    /// <summary>初回表示に必要なデータを取得し、画面の状態を準備します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。")]
    protected override Task OnInitializedAsync() => LoadAsync(false);

    /// <summary>表示対象の一覧または状態を取得して画面へ反映します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task LoadAsync(bool next)
    {
        busy = true;
        try
        {
            WorkflowListPage? page = await Api.PostAsync<WorkflowListPage>("/api/workflows/query", new WorkflowListQuery(Cursor: next ? cursor
                                                                                                                                      : null), cancellationToken: UiCancellation).ConfigureAwait(true);
            if (page is null)
            {
                throw new InvalidOperationException();
            }

            entries = page.Entries;
            cursor = page.Cursor;
            message = null;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException)
        {
            message = Ui.Deferred("The workflow page could not be retrieved.");
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>選択した項目を親画面へ通知します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task ChooseAsync(ChangeEventArgs args)
    {
        WorkflowListSummary? selected = entries.FirstOrDefault(item => item.Id is not null && string.Equals(item.Id, args.Value?.ToString(), StringComparison.Ordinal));
        if (selected is not null)
        {
            await Selected.InvokeAsync(selected).ConfigureAwait(true);
        }
    }
}
