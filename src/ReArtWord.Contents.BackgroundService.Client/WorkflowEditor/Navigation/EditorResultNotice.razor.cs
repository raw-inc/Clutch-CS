using Microsoft.AspNetCore.Components;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Navigation;

/// <summary>操作結果を一定時間表示し、読取り中や選択中は自動で閉じません。</summary>
// 抑制理由: Razorが生成する公開コンポーネント型です。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開partialの型です。")]
public sealed partial class EditorResultNotice
{
    /// <summary>閉じる操作を持たず、背面へポインター操作を通す1行表示にします。</summary>
    [Parameter]
    public bool Compact
    {
        get; set;
    }

    /// <summary>操作ごとに交換する表示文です。翻訳変更だけでは表示時間をリセットしません。</summary>
    [Parameter]
    public Func<string>? Message
    {
        get; set;
    }

    /// <summary>操作履歴を残したまま、表示中の一時通知を解除します。</summary>
    [Parameter]
    public EventCallback Dismissed
    {
        get; set;
    }

    /// <summary>継続中の問題は時間では消さず、発行元が復帰を確認して解除します。</summary>
    [Parameter]
    public bool Persistent
    {
        get; set;
    }

    /// <summary>通知元が解除した後も、退出表示が終わるまで内容を保持します。</summary>
    [Parameter]
    public bool Leaving
    {
        get; set;
    }

    /// <summary>退出表示を終えたカードだけを表示集合から取り除きます。</summary>
    [Parameter]
    public EventCallback Closed
    {
        get; set;
    }

    private readonly CancellationTokenSource lifetime = new();
    private Task expiry = Task.CompletedTask;
    private Task exit = Task.CompletedTask;
    private Func<string>? observed;
    private bool visible, hovered, focused, exitStarted;
    private int remaining;

    /// <summary>通知の有無だけを確認し、時間経過でグラフ全体を再描画しません。</summary>
    // 抑制理由: 開始した監視をexpiryで保持し、DisposeAsyncで取消後にawaitします。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW511", Justification = "コンポーネントがexpiryの寿命を所有し、破棄時にawaitします。")]
    protected override void OnInitialized() => expiry = ExpireAsync(lifetime.Token);

    /// <summary>退出スタイルを描画した後から、DOMを外すまでの短い猶予を計ります。</summary>
    // 抑制理由: 退出待機はexitで所有し、DisposeAsyncで取消後にawaitします。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW511", Justification = "退出待機をフィールドで所有し、破棄時に終了を確認します。")]
    protected override void OnAfterRender(bool firstRender)
    {
        if (Leaving && !exitStarted)
        {
            exitStarted = true;
            exit = CloseAfterExitAsync(lifetime.Token);
        }
    }

    /// <summary>新しい操作だけに8秒の表示時間を与えます。</summary>
    protected override void OnParametersSet()
    {
        if (Compact)
        {
            hovered = focused = false;
        }
        if (ReferenceEquals(Message, observed))
        {
            return;
        }
        bool previous = visible;
        observed = Message;
        visible = Message is not null;
        remaining = 8;
        if (!visible || !previous)
        {
            hovered = focused = false;
        }
    }

    /// <summary>閉じる操作と期限切れで、同じ表示解除を使用します。</summary>
    /// <returns>親が保持する一時表示の解除です。</returns>
    // 抑制理由: UIイベントの署名であり、I/Oを開始せず親へ完了を通知します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIイベントの固定署名で親へ表示解除だけを通知します。")]
    private async Task DismissAsync()
    {
        await Dismissed.InvokeAsync().ConfigureAwait(true);
    }

    /// <summary>退出中の読取り専用表示を解放し、画面を離れた場合は通知しません。</summary>
    /// <returns>退出完了またはコンポーネントの終了です。</returns>
    private async Task CloseAfterExitAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(240), cancellationToken).ConfigureAwait(true);
            await InvokeAsync(async () =>
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    visible = false;
                    await Closed.InvokeAsync().ConfigureAwait(true);
                }
            }).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>ホバー・フォーカス中は表示時間を消費せず、古い通知で新しい結果を消しません。</summary>
    /// <returns>画面終了までの期限確認です。</returns>
    private async Task ExpireAsync(CancellationToken cancellationToken = default)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(true))
            {
                if (!visible || Leaving || Persistent || hovered || focused || 0 < --remaining)
                {
                    continue;
                }
                Func<string>? expired = observed;
                await InvokeAsync(async () =>
                {
                    if (!cancellationToken.IsCancellationRequested && ReferenceEquals(expired, observed))
                    {
                        await DismissAsync().ConfigureAwait(true);
                        StateHasChanged();
                    }
                }).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>別画面へ移動した後に結果カードを更新しないよう監視を終了します。</summary>
    /// <returns>所有する監視の終了です。</returns>
    // 抑制理由: IAsyncDisposableの固定署名で、所有トークンを取消します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "IAsyncDisposableの固定署名で所有する処理を取消します。")]
    public override async ValueTask DisposeAsync()
    {
        try
        {
            await lifetime.CancelAsync().ConfigureAwait(true);
            await expiry.ConfigureAwait(true);
            await exit.ConfigureAwait(true);
        }
        finally
        {
            lifetime.Dispose();
            await base.DisposeAsync().ConfigureAwait(true);
            GC.SuppressFinalize(this);
        }
    }
}
