using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using ReArtWord.Contents.BackgroundService.Client.Overview.Hosts;
using ReArtWord.Contents.BackgroundService.Client.Overview.Telemetry;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Navigation;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;

namespace ReArtWord.Contents.BackgroundService.Client.Overview;

/// <summary>選択クラスタのPC実測・実行頻度・ログを表示します。</summary>
// 抑制理由: Razor が生成する public partial の公開範囲を維持します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor が生成する公開 partial 宣言と同じコンポーネント型を構成するため。")]
public sealed partial class DashboardOverview : IAsyncDisposable
{
    private static string EnterKey => "Enter";

    /// <summary>フォーカスしたログをキーボードでも開きます。</summary>
    private void LogKey(KeyboardEventArgs args, WorkflowLogEntry entry)
    {
        if (string.Equals(args.Key, EnterKey, StringComparison.Ordinal))
        {
            OpenLog(entry);
        }
    }

    [Inject] private NavigationManager Navigation
    {
        get; set;
    } = null!;

    /// <summary>ログを取得したクラスタから、記録されたWFとノードを開きます。</summary>
    private void OpenLog(WorkflowLogEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.WorkflowId) && telemetry is not null)
        {
            Navigation.NavigateTo(new EditorWorkflowLocation(entry.WorkflowId, entry.NodeId, telemetry.ClusterId, telemetry.SelectedPeerId).ToUri());
        }
    }

    [Inject]
    private ManagementApi Api
    {
        get;
        set;
    } = null!;

    [Inject]
    private ConnectionState Connection
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
    } = true;

    private DashboardTelemetry? telemetry;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool refreshing, disposed, wasActive;
    private bool detailedHosts;

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
            if (Connection.Current is { } status)
            {
                await telemetry.RefreshAsync(status, linked.Token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            refreshing = false;
            gate.Release();
            if (!disposed)
            {
                await InvokeAsync(StateHasChanged).ConfigureAwait(true);
            }
        }
    }

    /// <summary>次のピア集合を選択して実測を再取得します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task NextPeersAsync()
    {
        telemetry?.NextPeers();
        await RefreshAsync(cancellationToken: lifetime.Token).ConfigureAwait(true);
    }

    /// <summary>実体・取得結果・時刻から過去値として表示すべきか判定します。</summary>
    /// <returns>指定した条件を満たすかどうかです。</returns>
    private bool IsPrevious(OverviewPeer item) => item.Problem is not null || DashboardMetrics.IsStale(item.Latest, Api.UtcNow) || Connection.Current?.Peers.FirstOrDefault(peer => string.Equals(peer.PeerId, item.Peer.PeerId, StringComparison.Ordinal))?.InstanceId != item.Peer.InstanceId;

    /// <summary>未計測を区別した使用率の表示を作ります。</summary>
    /// <returns>小数点以下1桁の使用率、未計測ならダッシュです。</returns>
    private static string Percent(double? value) => value?.ToString("0.0", CultureInfo.CurrentCulture) ?? "—";

    /// <summary>温度の未計測を区別して摂氏表示を作ります。</summary>
    /// <returns>摂氏温度と単位、または未計測の表示です。</returns>
    private string Temperature(double? value) => value is null ? Ui["Not measured"]
                                                               : value.Value.ToString("0.0", CultureInfo.CurrentCulture) + " °C";

    /// <summary>取得不能を区別して通信量を毎秒の単位で表示します。</summary>
    /// <returns>MiB/sまたはKiB/sの通信量、取得不能ならダッシュです。</returns>
    private static string Rate(double? value) => value is null ? "—"
                                                               : 1048576 <= value.Value ? (value.Value / 1048576).ToString("0.00", CultureInfo.CurrentCulture) + " MiB/s"
                                                                                        : (value.Value / 1024).ToString("0.0", CultureInfo.CurrentCulture) + " KiB/s";

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
