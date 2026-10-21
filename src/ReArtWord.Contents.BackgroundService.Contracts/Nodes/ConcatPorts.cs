using System.Globalization;

namespace ReArtWord.Contents.BackgroundService.Contracts.Nodes;

/// <summary>結合入力の識別子と順番を保存・実行・編集で共有します。</summary>
public static class ConcatPorts
{
    /// <summary>編集と一回の実行で受け付ける入力数の上限です。</summary>
    public static int Maximum => 64;

    /// <summary>安定したノード識別子です。</summary>
    public static string NodeType => "data.concat";

    /// <summary>接続を外しても他の入力の番号を変えません。</summary>
    /// <returns>一から始まるポート識別子です。</returns>
    public static string Name(int index) => "input" + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>別名や上限外の入力を同じポートとして扱いません。</summary>
    /// <returns>正規入力の番号、または0です。</returns>
    public static int Index(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string prefix = "input";
        if (name.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(name.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int index)
            && 1 <= index && index <= Maximum && string.Equals(name, Name(index), StringComparison.Ordinal))
        {
            return index;
        }
        return 0;
    }

    /// <summary>二つから開始し、既存の全入力が埋まった場合だけ末尾を一つ増やします。</summary>
    /// <returns>途中の空き入力と既存番号を保持した表示数です。</returns>
    public static int VisibleCount(IEnumerable<string> supplied)
    {
        ArgumentNullException.ThrowIfNull(supplied);
        HashSet<int> used = supplied.Select(Index).Where(index => 0 < index).ToHashSet();
        int count = Math.Max(2, used.DefaultIfEmpty().Max());
        return count < Maximum && Enumerable.Range(1, count).All(used.Contains) ? count + 1
                                                                                : count;
    }
}
