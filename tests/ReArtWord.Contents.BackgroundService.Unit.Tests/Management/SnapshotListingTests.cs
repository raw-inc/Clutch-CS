using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.CoreService.Management;
using ReArtWord.Contents.BackgroundService.CoreService.Management.Snapshots;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>巨大スナップショット本文が一覧転送を膨張させないことを確認します。</summary>
public sealed class SnapshotListingTests
{
    /// <summary>大きい本文を持つ多数候補をメタデータだけで全ページ読み切れます。</summary>
    [Fact]
    public void PagesExcludePayloadAndKeepEveryIdentity()
    {
        NodeValue payload = NodeValue.FromObject(new { payload = new string('x', 2097152) });
        SnapshotRecord[] records = Enumerable.Range(0, 135).Select(index => new SnapshotRecord(index.ToString(System.Globalization.CultureInfo.InvariantCulture), "flow", "peer", ESnapshotKind.Configuration, DateTimeOffset.UnixEpoch.AddSeconds(index), payload, "hash", ["node"])).ToArray();
        HashSet<string> found = new(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            SnapshotListPage page = SnapshotListing.Build(records, new(Limit: 20, Cursor: cursor, MaximumBytes: 16384));
            byte[] wire = JsonSerializer.SerializeToUtf8Bytes(page, ContractJson.Options);
            Assert.True(wire.Length <= 16384);
            using JsonDocument json = JsonDocument.Parse(wire);
            foreach (JsonElement item in json.RootElement.GetProperty("entries").EnumerateArray())
            {
                Assert.False(item.TryGetProperty("value", out _));
                Assert.True(found.Add(item.GetProperty("id").GetString()!));
            }
            cursor = page.Cursor;
        } while (cursor is not null);
        Assert.Equal(135, found.Count);
    }

    /// <summary>巨大ノード集合を全体復元へ読み替えず、後続ページも取得できます。</summary>
    [Fact]
    public void OversizedScopeIsExplicitAndCursorAdvances()
    {
        SnapshotRecord large = new("large", "flow", "peer", ESnapshotKind.Configuration, DateTimeOffset.UnixEpoch.AddSeconds(1), NodeValue.Null, "hash", [new string('n', 40000)]);
        SnapshotRecord small = large with { Id = "small", CapturedAt = DateTimeOffset.UnixEpoch, NodeIds = ["node"] };
        SnapshotListPage first = SnapshotListing.Build([large, small], new(Limit: 1, MaximumBytes: 16384));
        Assert.True(first.Entries.Single().ScopeOmitted);
        Assert.Equal("snapshot.metadata.size", first.Entries.Single().DetailReason);
        Assert.NotNull(first.Cursor);
        SnapshotListPage second = SnapshotListing.Build([large, small], new(Cursor: first.Cursor, MaximumBytes: 16384));
        Assert.Equal("small", second.Entries.Single().Id);
        Assert.Null(second.Cursor);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(first, ContractJson.Options).Length <= 16384);
    }

    /// <summary>対象を明示絞込みし、不明cursorを先頭へ暗黙代替しません。</summary>
    [Fact]
    public void FilteringAndUnknownCursorAreExplicit()
    {
        SnapshotRecord record = new("id", "flow", "peer", ESnapshotKind.Configuration, DateTimeOffset.UnixEpoch, NodeValue.Null, "hash");
        Assert.Empty(SnapshotListing.Build([record], new(PeerId: "other")).Entries);
        Assert.Throws<ArgumentException>(() => SnapshotListing.Build([record], new(Cursor: "missing")));
    }
}
