using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Catalog;

/// <summary>ノード定義を分類して追加・ドラッグ選択に使用します。</summary>
// 抑制理由: Razor が生成する public partial の公開範囲を維持します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor が生成する公開 partial 宣言と同じコンポーネント型を構成するため。")]
public sealed partial class NodeCatalog
{
    /// <summary>閉じている間は検索状態を保持したまま一覧の再構築を省きます。</summary>
    [Parameter]
    public bool Visible
    {
        get; set;
    } = true;

    /// <summary>編集に使用するノード定義の一覧です。</summary>
    // 抑制理由: Blazorが描画ごとにParameterを設定するため、受信するコレクション参照のsetterを維持します。
    [Parameter, System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "Blazorが描画ごとにParameterを設定するため、受信するコレクション参照のsetterを維持します。")]
    public IReadOnlyList<NodeDescriptor> Catalog
    {
        get;
        set;
    } = [];

    /// <summary>選択したノードの追加を親画面へ通知します。</summary>
    [Parameter]
    public EventCallback<NodeDescriptor> Add
    {
        get;
        set;
    }

    /// <summary>親画面へ表示終了を通知します。</summary>
    [Parameter]
    public EventCallback Close
    {
        get;
        set;
    }

    /// <summary>カタログからのドラッグ開始を親画面へ通知します。</summary>
    [Parameter]
    public EventCallback<NodeDescriptor> DragStarted
    {
        get;
        set;
    }

    /// <summary>カタログからのドラッグ終了を親画面へ通知します。</summary>
    [Parameter]
    public EventCallback DragEnded
    {
        get;
        set;
    }

    private string search = string.Empty, category = string.Empty;
    private ECatalogLayout layout;
    private readonly HashSet<string> collapsed = new(StringComparer.Ordinal);
    private NodeDescriptor? selected;
    private IReadOnlyList<NodeDescriptor> Filtered => NodeQuickSearch.Find(Catalog, search, category.Length == 0 ? null
                                                                                                                 : category, Ui.NodeName, Ui.NodeDescription, Ui.Language);

    /// <summary>指定したカテゴリの表示開閉を切り替えます。</summary>
    private void Toggle(string key)
    {
        if (!collapsed.Add(key))
        {
            collapsed.Remove(key);
        }
    }

    /// <summary>ノードカタログの配置方式を表示名へ変換します。</summary>
    /// <returns>選択したカタログ配置方式の翻訳キーです。</returns>
    private static string LayoutLabel(ECatalogLayout option) => option switch
    {
        ECatalogLayout.Category => "By category",
        ECatalogLayout.Kind => "By kind",
        _ => "By name"
    };
}
