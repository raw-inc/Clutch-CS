using Microsoft.AspNetCore.Components;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Navigation;

/// <summary>通知の変更だけでカードを描画し、グラフの再描画と分離します。</summary>
// 抑制理由: Razorが生成する公開コンポーネント型です。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開partialの型です。")]
public sealed partial class EditorNotifications
{
    /// <summary>背面の操作を遮らない、1行の通知として表示します。</summary>
    [Parameter]
    public bool Compact
    {
        get; set;
    }

    [Inject]
    private EditorNoticeHub Notices
    {
        get; set;
    } = null!;

    // 抑制理由: 描画中の配列を変更せず、通知集合を不変の配列として交換します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "描画中の配列を変更せず、更新ごとに不変配列を交換します。")]
    private EditorNotice[] displayed = [];

    /// <summary>表示中だけ通知集合の変更を購読します。</summary>
    protected override void OnInitialized()
    {
        displayed = Notices.Items.ToArray();
        Notices.Changed += NoticesChanged;
    }

    /// <summary>現在の3件と退出中の最大3件だけを保持し、連続通知で古い表示を増やしません。</summary>
    private void NoticesChanged()
    {
        EditorNotice[] active = Notices.Items.ToArray();
        displayed = active.Concat(displayed.Where(previous => !active.Any(current => current.Id == previous.Id)).Take(3)).ToArray();
        RequestRenderFromEvent();
    }

    /// <summary>古いカードの退出で、新しい同用途の通知を消しません。</summary>
    private void RemoveClosed(Guid id) => displayed = displayed.Where(notice => notice.Id != id).ToArray();

    /// <summary>別の表示先や終了済み画面への通知を残しません。</summary>
    /// <returns>購読と描画の終了です。</returns>
    // 抑制理由: IAsyncDisposableの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "IAsyncDisposableの固定署名です。")]
    public override async ValueTask DisposeAsync()
    {
        Notices.Changed -= NoticesChanged;
        await base.DisposeAsync().ConfigureAwait(true);
        GC.SuppressFinalize(this);
    }
}
