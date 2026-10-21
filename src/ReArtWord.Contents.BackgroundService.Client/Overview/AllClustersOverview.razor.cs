using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using ReArtWord.Contents.BackgroundService.Client.Overview.Hosts;
using ReArtWord.Contents.BackgroundService.Client.Overview.Telemetry;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Navigation;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;

namespace ReArtWord.Contents.BackgroundService.Client.Overview;

/// <summary>観測中の全クラスタとPCの状態を有限並列で更新します。</summary>
// 抑制理由: Razor が生成する public partial の公開範囲を維持します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor が生成する公開 partial 宣言と同じコンポーネント型を構成するため。")]
public sealed partial class AllClustersOverview : IAsyncDisposable
{
    [Inject] private NavigationManager Navigation
    {
        get; set;
    } = null!;

    private static string EnterKey => "Enter";

    /// <summary>全クラスタの一覧でもログを取得した接続先へ移動します。</summary>
    private void OpenLog(string cluster, WorkflowLogEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.WorkflowId) && telemetry?.Observations.GetValueOrDefault(cluster) is { } observation)
        {
            Navigation.NavigateTo(new EditorWorkflowLocation(entry.WorkflowId, entry.NodeId, cluster, observation.SelectedPeerId).ToUri());
        }
    }

    /// <summary>フォーカスしたログをキーボードでも開きます。</summary>
    private void LogKey(KeyboardEventArgs args, string cluster, WorkflowLogEntry entry)
    {
        if (string.Equals(args.Key, EnterKey, StringComparison.Ordinal))
        {
            OpenLog(cluster, entry);
        }
    }

    [Inject]
    private ManagementApi Api
    {
        get;
        set;
    } = null!;

    /// <summary>表示中のタブとして定期取得を行うかどうかです。</summary>
    [Parameter]
    public bool Active
    {
        get;
        set;
    }

    private AllClustersTelemetry? telemetry;
    private bool refreshing, disposed, wasActive;
    private bool detailedHosts;
    private bool loaded;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>ホストカードも観測元の時計で同じ鮮度を判断します。</summary>
    /// <returns>今回の確認済み値として使えないかです。</returns>
    private bool IsPrevious(OverviewPeer peer) => peer.Problem is not null || DashboardMetrics.IsStale(peer.Latest, Api.UtcNow);

    /// <summary>初回表示に必要なデータを取得し、画面の状態を準備します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。")]
    protected override async Task OnInitializedAsync()
    {
        telemetry = new(Api);
        wasActive = Active;
        await RefreshAsync(cancellationToken: lifetime.Token).ConfigureAwait(true);
    }

    /// <summary>親から渡された対象に合わせて関連する状態を再確認します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。")]
    protected override async Task OnParametersSetAsync()
    {
        bool refresh = Active && !wasActive;
        wasActive = Active;
        if (refresh)
        {
            await RefreshAsync(cancellationToken: lifetime.Token).ConfigureAwait(true);
        }
    }

    /// <summary>表示対象の最新状態を取得して画面へ反映します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (disposed || !Active || telemetry is null)
        {
            return;
        }

        using CancellationTokenSource? linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        if (!await gate.WaitAsync(0, linked.Token).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            refreshing = true;
            await InvokeAsync(StateHasChanged).ConfigureAwait(true);
            await telemetry.RefreshAsync(linked.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            loaded = true;
            refreshing = false;
            gate.Release();
            if (!disposed)
            {
                await InvokeAsync(StateHasChanged).ConfigureAwait(true);
            }
        }
    }

    /// <summary>観測対象の件数を制限したページを進めて再取得します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task NextAsync()
    {
        telemetry?.NextPage();
        await RefreshAsync(cancellationToken: lifetime.Token).ConfigureAwait(true);
    }

    /// <summary>画面が所有する継続処理を終了し、保持する参照と資源を解放します。</summary>
    /// <returns>所有する処理と参照の解放完了です。</returns>
    // 抑制理由: BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。")]
    public override async ValueTask DisposeAsync()
    {
        try
        {
            disposed = true;
            await lifetime.CancelAsync().ConfigureAwait(true);
            await gate.WaitAsync().ConfigureAwait(true);
            gate.Release();
            gate.Dispose();
        }
        finally
        {
            lifetime.Dispose();
            await base.DisposeAsync().ConfigureAwait(true);
            GC.SuppressFinalize(this);
        }
    }
}
