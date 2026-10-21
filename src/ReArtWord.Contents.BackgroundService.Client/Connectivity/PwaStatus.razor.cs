using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace ReArtWord.Contents.BackgroundService.Client.Connectivity;

/// <summary>ブラウザーの画面資材とオフライン準備状態を表示します。</summary>
// 抑制理由: Razor が生成する public partial の公開範囲を維持します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor が生成する公開 partial 宣言と同じコンポーネント型を構成するため。")]
public sealed partial class PwaStatus : IAsyncDisposable
{
    [Inject]
    private IJSRuntime Script
    {
        get;
        set;
    } = null!;

    private readonly CancellationTokenSource lifetime = new();
    private Task polling = Task.CompletedTask;
    private PwaView? state;
    private Func<string>? message = () => "Checking UI asset storage.";

    /// <summary>初回表示で必要な状態と継続監視を準備します。</summary>
    // 抑制理由: 開始した監視Taskはフィールドで所有し、画面終了時に完了を確認します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW511", Justification = "継続監視Taskはpollingで保持し、DisposeAsyncで取消後にawaitします。")]
    protected override void OnInitialized()
    {
        message = Ui.Deferred("Checking UI asset storage.");
        polling = PollAsync();
    }

    /// <summary>画面の存続期間だけ状態を定期確認し、取消要求で終了します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task PollAsync()
    {
        using PeriodicTimer? timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            do
            {
                try
                {
                    state = await Script.InvokeAsync<PwaView>("contentsBrowser.pwaStatus", lifetime.Token).ConfigureAwait(true);
                    message = state.Error is not null ? Ui.Deferred("Offline registration is unconfirmed: {0}", state.Error)
                                                      : state.Ready ? Ui.Deferred("The published worker and UI asset cache are available.")
                                                                    : state.Controlled ? Ui.Deferred("The controlling worker’s published asset cache is unconfirmed. Close an older development page and reopen the new version.")
                                                                                       : Ui.Deferred("This page is not controlled by the published worker yet. After registration, retain your edits and reopen.");
                }
                catch (JSException)
                {
                    state = null;
                    message = Ui.Deferred("UI asset readiness could not be retrieved.");
                }

                await InvokeAsync(StateHasChanged).ConfigureAwait(true);
            }
            while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(true));
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
        }
        finally
        {
            lifetime.Dispose();
            await base.DisposeAsync().ConfigureAwait(true);
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>ブラウザーで確認した画面資材の準備状態です。</summary>
    private sealed record PwaView(bool Supported, bool Registered, bool Controlled, bool Waiting, bool Ready, string? Version, string? Error);
}
