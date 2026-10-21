using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Client.Overview.Telemetry;
using ReArtWord.Contents.BackgroundService.Client.Services;

namespace ReArtWord.Contents.BackgroundService.Client.Overview;

/// <summary>クラスタ・ピア・実体と実行版を対応付けて表示します。</summary>
// 抑制理由: Razor が生成する public partial の公開範囲を維持します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor が生成する公開 partial 宣言と同じコンポーネント型を構成するため。")]
public sealed partial class PeerList : IAsyncDisposable
{
    [Inject]
    private ManagementApi Api
    {
        get;
        set;
    } = null!;

    private PeerListTelemetry? telemetry;
    private bool disposed, refreshing, loaded;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>初回表示に必要なデータを取得し、画面の状態を準備します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。")]
    protected override async Task OnInitializedAsync()
    {
        telemetry = new(Api);
        await RefreshAsync(cancellationToken: lifetime.Token).ConfigureAwait(true);
    }

    /// <summary>表示対象の最新状態を取得して画面へ反映します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (disposed || telemetry is null)
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
