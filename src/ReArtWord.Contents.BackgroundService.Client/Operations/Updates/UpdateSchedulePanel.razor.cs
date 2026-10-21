using System.Text.Json;
using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Client.Operations.Journal;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Scheduling;

namespace ReArtWord.Contents.BackgroundService.Client.Operations.Updates;

/// <summary>起点Peerへ固定した予約照会・比較変更・開始前取消です。</summary>
// 抑制理由: Razor生成コードの公開宣言と一致させます。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor生成コードの公開宣言に合わせます。")]
public sealed partial class UpdateSchedulePanel
{
    [Inject]
    private ManagementApi Api
    {
        get; set;
    } = null!;

    [Inject]
    private OperationJournal Journal
    {
        get; set;
    } = null!;

    /// <summary>元の入力フォームへ確認した予約を渡します。</summary>
    [Parameter]
    public EventCallback<UpdateScheduleView> Edit
    {
        get; set;
    }

    private readonly List<UpdateScheduleView> schedules = [];
    private bool open, busy;
    private string? peer, cluster;
    private Func<string>? message;

    /// <summary>照会するPeerとクラスタを固定します。</summary>
    /// <returns>保存予約の初回取得です。</returns>
    private async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        peer = Api.SelectedPeerId;
        cluster = Api.SelectedClusterId;
        open = true;
        await RefreshAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>更新を再実行せず予約と元結果を取得します。</summary>
    /// <returns>この起点Peerの最新状態です。</returns>
    private async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            List<UpdateScheduleView> read = await Api.GetForPeerAsync<List<UpdateScheduleView>>("/api/updates/schedules", peer, cluster, cancellationToken).ConfigureAwait(true) ?? [];
            schedules.Clear();
            schedules.AddRange(read);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException)
        {
            message = Ui.Deferred("Update reservations could not be confirmed. Refresh the original peer.");
        }
    }

    /// <summary>別クラスタへ切り替えた画面から元予約を編集しません。</summary>
    /// <returns>元予約のフォームへの引渡しです。</returns>
    private async Task EditAsync(UpdateScheduleView view, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(cluster, Api.SelectedClusterId, StringComparison.Ordinal))
        {
            message = Ui.Deferred("Return to the reservation's cluster before editing.");
            return;
        }
        open = false;
        await Edit.InvokeAsync(view).ConfigureAwait(true);
    }

    /// <summary>確認した版の未開始予約だけを取消します。</summary>
    /// <returns>元操作IDから再照会できる取消結果です。</returns>
    private async Task CancelAsync(UpdateScheduleView view, CancellationToken cancellationToken = default)
    {
        busy = true;
        Guid operation = Guid.NewGuid();
        try
        {
            UpdateScheduleCancelRequest request = new(operation, view.Schedule.Plan.Update.OperationId, view.Version);
            await Journal.StartAsync(operation, "Cancel update reservation", "/api/updates/schedules/cancel", "/api/updates/schedules/operations/" + operation,
                request, targetPeer: peer, targetCluster: cluster, cancellationToken: cancellationToken).ConfigureAwait(true);
            message = Ui.Deferred("Operation {0}: {1}", operation, OperationJournal.Outcome(Journal.LastResult));
            await RefreshAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException or Microsoft.JSInterop.JSException or OperationCanceledException)
        {
            message = Ui.Deferred("Update is unconfirmed. Query operation {0}.", operation);
        }
        finally
        {
            busy = false;
        }
    }
}
