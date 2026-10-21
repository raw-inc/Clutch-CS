using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Navigation;

/// <summary>編集中のクラスタと管理対象ピアを切り替えます。</summary>
// 抑制理由: Razor が生成する public partial の公開範囲を維持します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor が生成する公開 partial 宣言と同じコンポーネント型を構成するため。")]
public sealed partial class EditorClusterTabs : IAsyncDisposable
{
    [Inject]
    private EditorNoticeHub Notices
    {
        get; set;
    } = null!;

    [Inject]
    private EditorDiagnostics Diagnostics
    {
        get; set;
    } = null!;

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

    [Inject]
    private EditorTargetSelection Selection
    {
        get;
        set;
    } = null!;

    /// <summary>管理対象の変更を親画面へ通知します。</summary>
    [Parameter]
    public EventCallback TargetChanged
    {
        get;
        set;
    }

    private EditorClusterObservations? observations;
    private IReadOnlyList<DiscoveredCluster> Clusters => observations?.Clusters ?? [];
    private readonly CancellationTokenSource lifetime = new();
    private Task polling = Task.CompletedTask;
    private bool busy;

    /// <summary>初回表示に必要なデータを取得し、画面の状態を準備します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。
    // 抑制理由: 開始した監視Taskはフィールドで所有し、画面終了時に完了を確認します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。"), System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW511", Justification = "継続監視Taskはpollingで保持し、DisposeAsyncで取消後にawaitします。")]
    protected override async Task OnInitializedAsync()
    {
        observations = new(Api);
        await RefreshAsync().ConfigureAwait(true);
        if (!lifetime.IsCancellationRequested)
        {
            polling = PollAsync();
        }
    }

    /// <summary>現在の観測集合から管理対象にできる候補を選びます。</summary>
    /// <returns>選択中の実体に一致するピア、または接続可能な候補です。</returns>
    private PeerInfo? Candidate(DiscoveredCluster cluster) => EditorClusterChoice.Suggest(cluster, Selection.PreviousPeer(cluster.ClusterId));

    /// <summary>指定した対象を選択し、関連する表示を更新します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task SelectAsync(string cluster, PeerInfo peer)
    {
        busy = true;
        Notices.Set("cluster-selection", null);
        try
        {
            await Selection.SelectClusterAsync(cluster, peer.PeerId, peer.InstanceId, lifetime.Token).ConfigureAwait(true);
            if (!Connection.Connected)
            {
                Notices.Set("cluster-selection", Ui.Deferred("The selected peer did not respond. The selection and draft are retained."));
            }
            await TargetChanged.InvokeAsync().ConfigureAwait(true);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or JSException or InvalidOperationException or OperationCanceledException)
        {
            if (!lifetime.IsCancellationRequested)
            {
                Notices.Set("cluster-selection", Ui.Deferred("Draft retention or the selected instance could not be confirmed. Refresh the candidates. No other peer was substituted."));
            }
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>表示対象の最新状態を取得して画面へ反映します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private Task RefreshAsync() => RefreshCoreAsync(true, lifetime.Token);

    /// <summary>観測の失敗遷移だけを通知し、成功復帰した警告を解除します。</summary>
    /// <returns>候補と通知の更新です。</returns>
    private async Task RefreshCoreAsync(bool manual, CancellationToken cancellationToken = default)
    {
        if (observations is null)
        {
            return;
        }
        bool previous = observations.Unavailable;
        await observations.RefreshAsync(cancellationToken).ConfigureAwait(true);
        if (cancellationToken.IsCancellationRequested || observations.Refreshing)
        {
            return;
        }
        if (!observations.Unavailable)
        {
            Notices.Set("cluster-observations", null);
        }
        else
        {
            if (!previous || manual)
            {
                Notices.Set("cluster-observations", Ui.Deferred("Current cluster observations are unavailable. Displayed candidates are from the last observation."), persistent: true);
            }
        }
    }

    /// <summary>画面の存続期間だけ状態を定期確認し、取消要求で終了します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task PollAsync()
    {
        using PeriodicTimer? timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(true))
            {
                if (!busy)
                {
                    await RefreshCoreAsync(false, lifetime.Token).ConfigureAwait(true);
                    if (lifetime.IsCancellationRequested)
                    {
                        break;
                    }
                    await InvokeAsync(StateHasChanged).ConfigureAwait(true);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>画面が所有する継続処理を終了し、保持する参照と資源を解放します。</summary>
    /// <returns>所有する処理と参照の解放完了です。</returns>
    // 抑制理由: BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。")]
    public override async ValueTask DisposeAsync()
    {
        try
        {
            await lifetime.CancelAsync().ConfigureAwait(true);
            await polling.ConfigureAwait(true);
            Notices.Set("cluster-observations", null);
            Notices.Set("cluster-selection", null);
        }
        finally
        {
            lifetime.Dispose();
            await base.DisposeAsync().ConfigureAwait(true);
            GC.SuppressFinalize(this);
        }
    }
}
