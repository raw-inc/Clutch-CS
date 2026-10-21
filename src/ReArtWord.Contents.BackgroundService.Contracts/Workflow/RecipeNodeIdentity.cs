namespace ReArtWord.Contents.BackgroundService.Contracts.Workflow;

/// <summary>配置ごとの内部ノードを、定義編集のノードと別の識別空間へ置きます。</summary>
public static class RecipeNodeIdentity
{
    /// <summary>内部配置に予約する接頭辞です。</summary>
    public static string Prefix => "$recipe/";

    /// <summary>入れ子でも同じ規則で一意な配置識別子を作ります。</summary>
    /// <returns>保存元のIDに区切り文字が含まれていても衝突しないIDです。</returns>
    public static string Child(string instance, string node)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(node);
        string parent = instance;
        if (!parent.StartsWith(Prefix, StringComparison.Ordinal))
        {
            parent = Prefix + Uri.EscapeDataString(parent);
        }
        return parent + "/" + Uri.EscapeDataString(node);
    }

    /// <summary>内部の処理中状態を配置ノードへ集約します。</summary>
    /// <returns>自身以外の子孫に属するかです。</returns>
    public static bool Contains(string instance, string node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.StartsWith(Child(instance, string.Empty), StringComparison.Ordinal);
    }
}
