using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.Overview.Telemetry;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;

namespace ReArtWord.Contents.BackgroundService.Client.Overview.Charts;

/// <summary>実測系列と時間軸の移動量を同時にブラウザーへ反映します。</summary>
// 抑制理由: Razor が生成する public partial の公開範囲を維持します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor が生成する公開 partial 宣言と同じコンポーネント型を構成するため。")]
public sealed partial class OverviewTrend : IAsyncDisposable
{
    [Inject]
    private IJSRuntime Js
    {
        get;
        set;
    } = null!;

    /// <summary>同じ実体の時刻順の実測バッファです。</summary>
    // 抑制理由: Blazorが描画ごとにParameterを設定するため、受信するコレクション参照のsetterを維持します。
    [Parameter, System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "Blazorが描画ごとにParameterを設定するため、受信するコレクション参照のsetterを維持します。")]
    public IReadOnlyList<ResourceSample> Samples
    {
        get;
        set;
    } = [];

    /// <summary>折れ線に使用する実測項目名です。</summary>
    [Parameter]
    public string Metric
    {
        get;
        set;
    } = "cpu.usage";

    /// <summary>実測項目を識別する観測対象名です。</summary>
    [Parameter]
    public string Target
    {
        get;
        set;
    } = "machine";

    /// <summary>折れ線のアクセシビリティ用の説明です。</summary>
    [Parameter]
    public string Label
    {
        get;
        set;
    } = "Measured series";

    /// <summary>縦軸の実測単位での上限です。使用率は100、転送量は期間内の自動目盛りです。</summary>
    [Parameter]
    public double Maximum
    {
        get;
        set;
    } = 100;

    /// <summary>線からの距離だけで透明度を変える、共有形状の描画段数です。</summary>
    private static int ShadowLayers => 24;

    private readonly string gradient = "usage-" + Guid.NewGuid().ToString("N");
    private string path = string.Empty;
    private ElementReference chart;
    private IJSObjectReference? module;
    private long? origin;
    private bool disposed;

    /// <summary>親から渡された値に合わせて表示用の状態を更新します。</summary>
    protected override void OnParametersSet()
    {
        path = DashboardMetrics.Trend(Samples, Metric, Target, Maximum);
        origin = Samples.Count == 0 ? null
                                    : Samples[^1].ObservedAt.ToUnixTimeMilliseconds();
    }

    /// <summary>実測の折れ線と同じ原点の移動量を、一回のJavaScript呼出しで反映します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。")]
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (disposed)
        {
            return;
        }

        await renderGate.WaitAsync(UiCancellation).ConfigureAwait(true);
        try
        {
            if (disposed)
            {
                return;
            }

            if (firstRender)
            {
                module = await Js.InvokeAsync<IJSObjectReference>("import", "./overview-trend.js").ConfigureAwait(true);
                if (disposed)
                {
                    return;
                }
            }

            if (disposed)
            {
                return;
            }

            if (module is not null)
            {
                await module.InvokeVoidAsync("update", UiCancellation, chart, path, origin).ConfigureAwait(true);
            }
        }
        finally
        {
            renderGate.Release();
        }
    }

    /// <summary>描画監視を解除してJavaScriptモジュールの参照を解放します。</summary>
    /// <returns>所有する処理と参照の解放完了です。</returns>
    // 抑制理由: BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。")]
    public override async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await renderGate.WaitAsync().ConfigureAwait(true);
        try
        {
            try
            {
                disposed = true;
                if (module is not null)
                {
                    try
                    {
                        await module.InvokeVoidAsync("unobserve", chart).ConfigureAwait(true);
                        await module.DisposeAsync().ConfigureAwait(true);
                    }
                    catch (JSDisconnectedException)
                    {
                    }
                }
            }
            finally
            {
                await base.DisposeAsync().ConfigureAwait(true);
                GC.SuppressFinalize(this);
            }
        }
        finally
        {
            renderGate.Release();
            renderGate.Dispose();
        }
    }

    private readonly SemaphoreSlim renderGate = new(1, 1);
}
