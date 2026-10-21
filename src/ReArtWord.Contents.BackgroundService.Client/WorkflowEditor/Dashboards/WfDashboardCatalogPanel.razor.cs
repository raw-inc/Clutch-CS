using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow.Dashboards;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Dashboards;

/// <summary>名前検索とページ取得だけを担当する参照ノードの追加パネルです。</summary>
// 抑制理由: Razorが生成する公開partialと一致させます。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開partialと一致させます。")]
public sealed partial class WfDashboardCatalogPanel
{
    /// <summary>参照カードの追加先です。</summary>
    [Parameter]
    public EventCallback<WfDashboardCatalogItem> Add
    {
        get; set;
    }

    /// <summary>パネルを閉じる操作です。</summary>
    [Parameter]
    public EventCallback Close
    {
        get; set;
    }

    private readonly List<WfDashboardCatalogItem> items = [];
    private string search = string.Empty;
    private string? problem;
    private int? next;
    private int generation;
    private bool busy;
    private CancellationTokenSource? searchCancellation;

    /// <summary>現在のクラスタだけから候補を取得します。</summary>
    /// <returns>最初のページの取得です。</returns>
    // 抑制理由: Blazorの固定署名でUiCancellationを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "Blazorの固定署名でUiCancellationを渡します。")]
    protected override Task OnInitializedAsync() => ReadAsync(false, UiCancellation);

    /// <summary>連続した入力を短くまとめ、古い検索応答は描画しません。</summary>
    /// <returns>現在の入力に対応する候補の取得です。</returns>
    // 抑制理由: 入力イベントの固定署名で画面寿命の取消しを所有します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "入力イベントの固定署名で画面寿命の取消しを所有します。")]
    private async Task SearchAsync(ChangeEventArgs args)
    {
        search = args.Value?.ToString() ?? string.Empty;
        generation++;
        if (searchCancellation is not null)
        {
            using CancellationTokenSource previous = searchCancellation;
            await previous.CancelAsync().ConfigureAwait(true);
        }
        searchCancellation = CancellationTokenSource.CreateLinkedTokenSource(UiCancellation);
        CancellationToken token = searchCancellation.Token;
        try
        {
            await Task.Delay(180, token).ConfigureAwait(true);
            await ReadAsync(false, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    /// <summary>表示名・物理IDだけのページを取り込み、後着ページを別検索へ混ぜません。</summary>
    /// <returns>候補または取得不能の表示です。</returns>
    private async Task ReadAsync(bool more, CancellationToken cancellationToken = default)
    {
        int requested = ++generation;
        string? cluster = Api.SelectedClusterId, peer = Api.SelectedPeerId;
        busy = true;
        problem = null;
        try
        {
            int offset = 0;
            if (more)
            {
                offset = next ?? 0;
            }
            WfDashboardCatalogPage page = await Api.PostForPeerAsync<WfDashboardCatalogPage>("/api/dashboards/catalog", new WfDashboardCatalogQuery(search, offset), peer, cluster, cancellationToken: cancellationToken).ConfigureAwait(true)
                ?? throw new InvalidOperationException("The node catalog could not be confirmed.");
            if (requested != generation || !string.Equals(cluster, Api.SelectedClusterId, StringComparison.Ordinal) || !string.Equals(peer, Api.SelectedPeerId, StringComparison.Ordinal))
            {
                return;
            }
            if (!more)
            {
                items.Clear();
            }
            items.AddRange(page.Items.Where(item => !items.Any(old => old.Reference == item.Reference)));
            next = page.NextOffset;
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException or OperationCanceledException)
        {
            if (requested == generation && !cancellationToken.IsCancellationRequested)
            {
                problem = "The node catalog could not be confirmed.";
            }
        }
        finally
        {
            if (requested == generation)
            {
                busy = false;
            }
        }
    }

    /// <summary>未確定の検索を画面外へ残しません。</summary>
    /// <returns>取消しと基底画面の終了です。</returns>
    // 抑制理由: コンポーネント終了時に所有する検索を取り消します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "コンポーネント終了時に所有する検索を取り消します。")]
    public override async ValueTask DisposeAsync()
    {
        if (searchCancellation is not null)
        {
            await searchCancellation.CancelAsync().ConfigureAwait(true);
            searchCancellation.Dispose();
        }
        await base.DisposeAsync().ConfigureAwait(true);
    }
}
