namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;

/// <summary>主選択と複数選択を一つの集合で管理します。</summary>
internal sealed class GraphSelection
{
    private readonly HashSet<string> nodes = new(StringComparer.Ordinal);

    /// <summary>タブ切替でも選択のコピーを維持します。</summary>
    internal GraphClipboard Clipboard
    {
        get;
    } = new();

    /// <summary>既存の詳細欄が表示するノードです。</summary>
    internal string? Primary
    {
        get; private set;
    }

    /// <summary>現在選択されているノードです。</summary>
    internal IReadOnlySet<string> Nodes => nodes;

    /// <summary>同じ主選択でも集合が変わったら再描画します。</summary>
    internal int Version
    {
        get; private set;
    }

    /// <summary>通常選択または背景クリックで集合を置き換えます。</summary>
    internal void Single(string? id)
    {
        nodes.Clear();
        Primary = id;
        if (id is not null)
        {
            nodes.Add(id);
        }
        Version++;
    }

    /// <summary>Ctrlクリックは一つのノードだけを加減します。</summary>
    internal void Toggle(string id)
    {
        if (!nodes.Remove(id))
        {
            nodes.Add(id);
            Primary = id;
        }
        else
        {
            if (string.Equals(Primary, id, StringComparison.Ordinal))
            {
                Primary = nodes.LastOrDefault();
            }
        }
        Version++;
    }

    /// <summary>範囲選択は既存集合に追加します。</summary>
    internal void Add(IEnumerable<string> ids)
    {
        foreach (string id in ids)
        {
            if (nodes.Add(id))
            {
                Primary = id;
            }
        }
        Version++;
    }

    /// <summary>文書編集で消えたノードを選択に残しません。</summary>
    internal void Retain(IEnumerable<string> ids)
    {
        nodes.IntersectWith(ids);
        if (Primary is not null && !nodes.Contains(Primary))
        {
            Primary = nodes.LastOrDefault();
        }
        Version++;
    }
}
