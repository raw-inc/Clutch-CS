using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Configuration;

/// <summary>反映対象の実変更と秘密値の非表示を両立します。</summary>
public sealed class ConfigurationDifferencesTests
{
    private static readonly string[] targetPeers = ["b"];

    /// <summary>同じ件数の配線変更も、変わった対象を確認できます。</summary>
    [Fact]
    public void ChangedWireShowsActualDestination()
    {
        var before = new { wires = new[] { new { fromNode = "source", toNode = "old" } } };
        var after = new { wires = new[] { new { fromNode = "source", toNode = "new" } } };
        (IReadOnlyList<ConfigurationDifference> changes, bool truncated) = ConfigurationDifferences.Compare(before, after);
        ConfigurationDifference change = Assert.Single(changes);
        Assert.Equal("/wires/0/toNode", change.Path);
        Assert.Equal("\"old\"", change.Before);
        Assert.Equal("\"new\"", change.After);
        Assert.False(truncated);
    }

    /// <summary>新規ノードの追加内容を示しつつ、秘密名の値は返しません。</summary>
    [Fact]
    public void AddedNodeShowsFieldsWithoutRevealingSecrets()
    {
        JsonElement before = JsonSerializer.SerializeToElement(new { nodes = new { } });
        JsonElement after = JsonSerializer.SerializeToElement(new { nodes = new { added = new { type = "http.request", target = new { peers = targetPeers }, settings = new { url = "https://example.test", token = "hidden" } } } });
        (IReadOnlyList<ConfigurationDifference> changes, bool _) = ConfigurationDifferences.Compare(before, after);
        Assert.Contains(changes, change => string.Equals(change.Path, "/nodes/added/type", StringComparison.Ordinal) && string.Equals(change.After, "\"http.request\"", StringComparison.Ordinal));
        Assert.Contains(changes, change => string.Equals(change.Path, "/nodes/added/target/peers/0", StringComparison.Ordinal) && string.Equals(change.After, "\"b\"", StringComparison.Ordinal));
        ConfigurationDifference secret = Assert.Single(changes, change => change.Path.EndsWith("/token", StringComparison.Ordinal));
        Assert.True(secret.Omitted);
        Assert.DoesNotContain("hidden", secret.After, StringComparison.Ordinal);
    }
}
