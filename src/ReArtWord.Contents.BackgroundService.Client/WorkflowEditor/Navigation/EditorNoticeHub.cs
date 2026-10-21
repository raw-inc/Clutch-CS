namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Navigation;

/// <summary>ヘッダーの観測とエディタ操作の通知を、キャンバス上の同じ表示先へ届けます。</summary>
internal sealed class EditorNoticeHub
{
    private readonly List<EditorNotice> items = [];

    /// <summary>新しい通知を先頭にした最大3件です。</summary>
    internal IReadOnlyList<EditorNotice> Items => items;

    /// <summary>表示先だけへ更新を通知します。</summary>
    internal event Action? Changed;

    /// <summary>同じ用途の古い通知を交換し、他の操作結果を消しません。</summary>
    internal void Set(string channel, Func<string>? text, bool persistent = false)
    {
        // 正常なWF読取りなどの空本文は通知なしとして扱い、見出しだけのカードを作りません。
        if (text is not null && string.IsNullOrWhiteSpace(text()))
        {
            text = null;
        }
        int removed = items.RemoveAll(item => string.Equals(item.Channel, channel, StringComparison.Ordinal));
        if (text is not null)
        {
            items.Insert(0, new(Guid.NewGuid(), channel, text, persistent));
            if (3 < items.Count)
            {
                items.RemoveAt(items.Count - 1);
            }
        }
        if (text is not null || removed != 0)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>古いタイマーや閉じる操作で、新しく交換した通知を消しません。</summary>
    internal void Dismiss(Guid id)
    {
        if (items.RemoveAll(item => item.Id == id) != 0)
        {
            Changed?.Invoke();
        }
    }
}
