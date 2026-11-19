using Microsoft.AspNetCore.Components.Web;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Navigation;

/// <summary>タブの操作メニューから編集対象を閉じたり削除したりします。</summary>
public sealed partial class EditorWorkflowTabs
{
    /// <summary>Escapeでタブ操作を取り消します。</summary>
    private void MenuKey(KeyboardEventArgs args)
    {
        string escape = "Escape";
        if (string.Equals(args.Key, escape, StringComparison.Ordinal))
        {
            menuId = null;
        }
    }

    /// <summary>草稿保持が完了したときだけタブを隠します。</summary>
    /// <returns>非表示設定の保存です。</returns>
    // 抑制理由: UIイベントの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIイベントの固定署名でUiCancellationを使用します。")]
    private async Task HideAsync()
    {
        if (Busy || menuId is not { } id)
        {
            return;
        }
        menuId = null;
        try
        {
            await Hide.InvokeAsync(id).ConfigureAwait(true);
            retained.Hide(id);
            RebuildTabs();
            await SaveLayoutAsync(UiCancellation).ConfigureAwait(true);
        }
        catch (InvalidOperationException)
        {
            preferenceProblem = "tabs.hideFailed";
        }
    }

    /// <summary>WF本体の削除は既存の確認ダイアログへ委譲します。</summary>
    /// <returns>削除確認の表示です。</returns>
    // 抑制理由: UIイベントの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIイベントの固定署名です。")]
    private Task DeleteAsync()
    {
        string? id = menuId;
        menuId = null;
        return !Busy && id is not null ? Delete.InvokeAsync(id)
                                       : Task.CompletedTask;
    }

    /// <summary>メニューの移動先を可視タブの範囲に限定します。</summary>
    /// <returns>隣の表示タブまたは端です。</returns>
    private string? Neighbor(int offset) => retained.Neighbor(menuId, offset);
}
