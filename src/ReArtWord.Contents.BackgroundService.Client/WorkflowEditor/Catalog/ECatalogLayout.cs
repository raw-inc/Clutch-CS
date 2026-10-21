namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Catalog;

/// <summary>ノードを探すための表示上の分類です。</summary>
internal enum ECatalogLayout
{
    /// <summary>用途のカテゴリでまとめます。</summary>
    Category,

    /// <summary>値構築・継続入力・一回処理でまとめます。</summary>
    Kind,

    /// <summary>表示言語の名前順で並べます。</summary>
    Name,
}
