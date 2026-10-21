using System.Text;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Observability;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Control;

/// <summary>画面の照会より速い入力も捕捉し、履歴の件数・本文・寿命を制限します。</summary>
public sealed class PreviewHistoryTests
{
    /// <summary>連続した同じ値も別の取得として残し、最新50件だけを新しい順で返します。</summary>
    /// <returns>実Previewノードから履歴取得までの確認です。</returns>
    [Fact]
    public async Task BurstCapturesKeepFiftyDistinctArrivalsAsync()
    {
        NodePreviewStore store = new();
        PreviewNode node = new(store, new([]));
        for (int index = 0; index < 75; index++)
        {
            await node.ExecuteAsync(NodeTestContext.Create(value: NodeValue.FromObject(index / 2))).ConfigureAwait(true);
        }
        IReadOnlyList<NodePreviewHistoryEntry> history = store.ReadHistory(new("workflow", "node"));
        Assert.Equal(50, history.Count);
        Assert.Equal(Enumerable.Range(26, 50).Reverse().Select(number => (long)number), history.Select(row => row.Number));
        Assert.Equal(50, history.Select(row => row.Snapshot.Id).Distinct().Count());
        Assert.Equal("37", history[0].Text);
        Assert.Equal("36", history[1].Text);
        Assert.Equal(history[1].Text, history[2].Text);
        Assert.Equal(history[0].Snapshot, Assert.Single(store.Read(new("workflow", ["node"]))));
    }

    /// <summary>履歴だけに媒体を複製せず、失敗・空文字・nullと長いUnicodeを区別します。</summary>
    /// <returns>値の表示と媒体本文の寿命の確認です。</returns>
    [Fact]
    public async Task SummariesBoundUnicodeAndNeverRetainOldMediaAsync()
    {
        NodePreviewStore store = new();
        PreviewNode node = new(store, new([]));
        NodeExecutionContext context = NodeTestContext.Create();
        await node.ExecuteAsync(context with { Inputs = new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromBytes(Convert.FromHexString("89504E470D0A1A0A")) } }).ConfigureAwait(true);
        NodePreviewSnapshot media = Assert.Single(store.Read(new("workflow", ["node"])));
        await node.ExecuteAsync(NodeTestContext.Create(value: NodeValue.Null)).ConfigureAwait(true);
        await node.ExecuteAsync(NodeTestContext.Create(value: NodeValue.FromObject(string.Empty))).ConfigureAwait(true);
        await node.ExecuteAsync(NodeTestContext.Create(value: NodeValue.FromObject(new string('あ', 1023) + "😀end"))).ConfigureAwait(true);
        await node.ExecuteAsync(NodeTestContext.Create(value: NodeValue.FromBytes(new byte[] { 1, 2, 3 }))).ConfigureAwait(true);
        IReadOnlyList<NodePreviewHistoryEntry> rows = store.ReadHistory(new("workflow", "node"));
        Assert.Equal("preview.format", rows[0].Snapshot.Problem);
        Assert.Null(rows[0].Text);
        Assert.True(rows[1].TextTruncated);
        Assert.Equal(new string('あ', 1023), rows[1].Text);
        Assert.Equal(string.Empty, rows[2].Text);
        Assert.Equal("null", rows[3].Text);
        Assert.Equal("image/png", rows[4].Snapshot.MediaType);
        Assert.Equal(8, rows[4].Snapshot.Length);
        Assert.Null(rows[4].Text);
        Assert.Throws<KeyNotFoundException>(() => store.ReadChunk(new(media.Id, 0)));
    }

    /// <summary>別WF・別ノードを混ぜず、旧版は取得時の版として表示し、5分で破棄します。</summary>
    [Fact]
    public void HistoryRespectsIdentityRevisionAndExpiration()
    {
        LogTestClock clock = new();
        NodePreviewStore store = new(clock);
        NodeExecutionContext context = NodeTestContext.Create();
        store.Publish(context with { Revision = "old" }, "text/plain", "old"u8.ToArray());
        store.Publish(context with { WorkflowId = "other" }, "text/plain", "other"u8.ToArray());
        store.Publish(context with { NodeId = "second" }, "text/plain", "second"u8.ToArray());
        clock.Advance(TimeSpan.FromMinutes(4));
        store.Publish(context with { Revision = "new" }, "text/plain", "new"u8.ToArray());
        Assert.Equal(["new", "old"], store.ReadHistory(new("workflow", "node")).Select(row => row.Snapshot.Revision));
        clock.Advance(TimeSpan.FromMinutes(1));
        NodePreviewHistoryEntry recent = Assert.Single(store.ReadHistory(new("workflow", "node")));
        Assert.Equal(2, recent.Number);
        Assert.Equal("new", recent.Text);
        Assert.Empty(store.ReadHistory(new("other", "node")));
        Assert.Empty(store.ReadHistory(new("workflow", "second")));
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Empty(store.ReadHistory(new("workflow", "node")));
    }

    /// <summary>同時到着にも別番号を割り当て、ノード保持数の上限で履歴も解放します。</summary>
    /// <returns>履歴番号と最新値の整合性です。</returns>
    [Fact]
    public async Task ConcurrentArrivalsAndNodeEvictionStayBoundedAsync()
    {
        LogTestClock clock = new();
        NodePreviewStore store = new(clock);
        NodeExecutionContext context = NodeTestContext.Create();
        await Task.WhenAll(Enumerable.Range(0, 100).Select(index => Task.Run(() => store.Publish(context, "text/plain",
            Encoding.UTF8.GetBytes(index.ToString(System.Globalization.CultureInfo.InvariantCulture)))))).ConfigureAwait(true);
        IReadOnlyList<NodePreviewHistoryEntry> rows = store.ReadHistory(new("workflow", "node"));
        Assert.Equal(Enumerable.Range(51, 50).Reverse().Select(number => (long)number), rows.Select(row => row.Number));
        Assert.Equal(rows[0].Snapshot.Id, Assert.Single(store.Read(new("workflow", ["node"]))).Id);
        for (int index = 0; index < 128; index++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(1));
            store.Publish(context with { NodeId = index.ToString(System.Globalization.CultureInfo.InvariantCulture) }, "text/plain", "small"u8.ToArray());
        }
        Assert.Empty(store.ReadHistory(new("workflow", "node")));
        Assert.Single(store.ReadHistory(new("workflow", "127")));
    }
}
