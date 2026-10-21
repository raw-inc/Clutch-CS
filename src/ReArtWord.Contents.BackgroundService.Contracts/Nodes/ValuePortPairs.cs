using System.Globalization;

namespace ReArtWord.Contents.BackgroundService.Contracts.Nodes;

/// <summary>同じ番号の入力と出力を持つ可変ポートの名前と表示数を共有します。</summary>
public static class ValuePortPairs
{
    /// <summary>最初の値は既存のvalue接続を維持します。</summary>
    /// <returns>入出力に共通するポート名です。</returns>
    public static string Name(int index)
    {
        if (index == 1)
        {
            return "value";
        }
        return "value" + index.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>他の可変ポートと同じ番号の範囲・表記を受け付けます。</summary>
    /// <returns>値の番号、対象外なら0です。</returns>
    public static int Index(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string prefix = "value";
        if (string.Equals(name, prefix, StringComparison.Ordinal))
        {
            return 1;
        }
        if (name.StartsWith(prefix, StringComparison.Ordinal))
        {
            int index = ConcatPorts.Index("input" + name[prefix.Length..]);
            if (2 <= index)
            {
                return index;
            }
        }
        return 0;
    }

    /// <summary>使用した入力の次を開き、出力だけ接続されている番号も保持します。</summary>
    /// <returns>途中の空欄を詰めない表示数です。</returns>
    public static int VisibleCount(IEnumerable<string> supplied, IEnumerable<string> connectedOutputs)
    {
        ArgumentNullException.ThrowIfNull(supplied);
        ArgumentNullException.ThrowIfNull(connectedOutputs);
        string[] inputs = supplied.Select(Index).Where(index => 0 < index).Select(ConcatPorts.Name).ToArray();
        int count = 1;
        if (inputs.Length != 0)
        {
            count = ConcatPorts.VisibleCount(inputs);
        }
        int outputs = connectedOutputs.Select(port => Index(PropertyPorts.Parse(port)?.Output ?? port)).DefaultIfEmpty().Max();
        return Math.Max(count, outputs);
    }
}
