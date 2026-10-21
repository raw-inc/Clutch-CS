using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Client.Operations.Execution;

/// <summary>取得元を固定した実行一覧から、元の実行の取消と結果照会を行います。</summary>
// 抑制理由: Razorが生成する公開宣言に合わせます。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開宣言と同じ型を構成するため。")]
public sealed partial class ActiveRunsPanel
{
    [Inject]
    private ManagementApi Api
    {
        get; set;
    } = null!;

    private ActiveRunsPage? page;
    private WorkflowRunStatus? result;
    private string? peer, cluster;
    private Func<string>? message;
    private bool busy;

    /// <summary>表示対象を固定し、そのPeerの実行一覧を取得します。</summary>
    /// <returns>初回取得の完了。</returns>
    // 抑制理由: Blazorの固定署名に従い、通信には画面の取消トークンを使用します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "Blazorの固定署名。通信にはUiCancellationを渡します。")]
    protected override Task OnInitializedAsync()
    {
        peer = Api.SelectedPeerId;
        cluster = Api.SelectedClusterId;
        return LoadAsync(null, UiCancellation);
    }

    /// <summary>一覧を更新します。接続先変更時は親画面がコンポーネントを作り直します。</summary>
    /// <returns>取得したページの表示。</returns>
    private async Task LoadAsync(Guid? after, CancellationToken cancellationToken = default)
    {
        if (busy || peer is null || cluster is null)
        {
            return;
        }
        busy = true;
        try
        {
            page = await Api.PostForPeerAsync<ActiveRunsPage>("/api/runs/active/query", new ActiveRunsQuery(After: after), peer, cluster,
                cancellationToken: cancellationToken).ConfigureAwait(true);
            message = null;
        }
        catch (Exception error) when (error is HttpRequestException or System.Text.Json.JsonException or OperationCanceledException)
        {
            page = null;
            message = Ui.Deferred("The running workflow list could not be retrieved.");
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>取消の受付から実停止を推測せず、元の実行結果を照会します。</summary>
    /// <returns>元の実行の最新状態。</returns>
    private async Task CheckAsync(Guid id, CancellationToken cancellationToken = default)
    {
        try
        {
            result = await Api.GetForPeerAsync<WorkflowRunStatus>("/api/runs/" + id, peer, cluster, cancellationToken).ConfigureAwait(true);
            message = null;
        }
        catch (Exception error) when (error is HttpRequestException or System.Text.Json.JsonException or OperationCanceledException)
        {
            result = null;
            message = Ui.Deferred("The original execution result could not be confirmed.");
        }
    }
}
