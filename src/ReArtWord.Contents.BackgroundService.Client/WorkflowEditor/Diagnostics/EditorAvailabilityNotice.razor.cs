using System.Text.Json;
using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.Availability;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;

/// <summary>編集中のワークフローの保存可否とローカル制限を確認します。</summary>
// 抑制理由: Razor が生成する public partial の公開範囲を維持します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor が生成する公開 partial 宣言と同じコンポーネント型を構成するため。")]
public sealed partial class EditorAvailabilityNotice
{
    [Inject]
    private ManagementApi Api
    {
        get;
        set;
    } = null!;

    /// <summary>確認または編集するワークフローの識別子です。</summary>
    [Parameter]
    public string WorkflowId
    {
        get;
        set;
    } = string.Empty;

    // 抑制理由: 取得したスナップショットの参照を交換し、子画面や開始済み処理が保持する旧コレクションを変更しないため。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "取得したスナップショットの参照を交換し、子画面や開始済み処理が保持する旧コレクションを変更しないため。")]
    private List<AvailabilityState> states = [];
    private Func<string>? message;

    /// <summary>選択したデータまたは元の操作結果を取得します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task ReadAsync()
    {
        (string? SelectedClusterId, string? SelectedPeerId, string WorkflowId) owner = (Api.SelectedClusterId, Api.SelectedPeerId, WorkflowId);
        try
        {
            List<AvailabilityState> collected = [];
            string? cursor = null;
            do
            {
                AvailabilityListPage? page = await Api.PostAsync<AvailabilityListPage>("/api/availability/query", new AvailabilityListQuery(Cursor: cursor), cancellationToken: UiCancellation).ConfigureAwait(true);
                if (page is null)
                {
                    throw new InvalidOperationException(Ui["Status has not been confirmed."]);
                }

                collected.AddRange(page.Entries.Where(item => string.Equals(item.DatasetId, "workflow:" + owner.WorkflowId, StringComparison.Ordinal)));
                cursor = page.Cursor;
            }
            while (cursor is not null);
            if (owner != (Api.SelectedClusterId, Api.SelectedPeerId, WorkflowId))
            {
                return;
            }

            states = collected;
            message = states.Count == 0 ? Ui.Deferred("This peer has not reported the workflow's storage status. Sharing is not confirmed.")
                                        : null;
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException)
        {
            message = Ui.Deferred("Saved, active and recoverable versions could not be confirmed.");
        }
    }
}
