using System.Text;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;

namespace ReArtWord.Contents.BackgroundService.Nodes.Data.Collections;

/// <summary>集合処理の走査・比較・出力保持を有限の一件予算へまとめます。</summary>
internal sealed class CollectionBudget(NodeSettings settings, CancellationToken cancellationToken)
{
    private readonly int maximumItems = settings.ReadInt32("maxItems", 10000);
    private readonly int maximumBytes = settings.ReadInt32("maxBytes", 16777216);
    private readonly int maximumWork = settings.ReadInt32("maxWork", 1000000);
    private int work, items;
    private long bytes;

    /// <summary>実施する一比較・一走査の前に取消と計算上限を検証します。</summary>
    internal void Step()
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (maximumWork < ++work)
        {
            throw new FormatException("collection.workLimit");
        }
    }

    /// <summary>入力の保持上限を開始前に確認します。</summary>
    internal void CheckInput(int count, long byteCount)
    {
        if (maximumItems < count || maximumBytes < byteCount)
        {
            throw new FormatException("collection.inputLimit");
        }
    }

    /// <summary>結果を追加する前に総出力の件数とUTF8長を確認します。</summary>
    internal void Add(JsonArray output, JsonNode? value)
    {
        Step();
        bytes += Encoding.UTF8.GetByteCount(value?.ToJsonString() ?? "null") + 1;
        if (maximumItems < ++items || maximumBytes < bytes + 2)
        {
            throw new FormatException("collection.outputLimit");
        }
        output.Add(value);
    }
}
