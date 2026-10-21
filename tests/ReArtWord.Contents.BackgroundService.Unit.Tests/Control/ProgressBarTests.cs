using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Control;

/// <summary>数値の混在と最新値・失敗のプレビュー境界を検証します。</summary>
public sealed class ProgressBarTests
{
    /// <summary>整数と小数を混在させ、ゼロ総数と超過時も有限のバー表示になります。</summary>
    /// <returns>出力なしの終端と取得した数値の検証です。</returns>
    [Theory]
    [InlineData("10", "2.5", 25)]
    [InlineData("2.5", "1", 40)]
    [InlineData("0", "0", 0)]
    [InlineData("2", "3", 100)]
    public async Task CapturesMixedNumbersWithoutOutputsAsync(string total, string current, double percentage)
    {
        NodePreviewStore store = new();
        ProgressBarNode node = new(store);
        NodeExecutionContext context = Context(total, current);
        NodeResult result = await node.ExecuteAsync(context).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Empty(result.Outputs);
        Assert.Empty(node.Descriptor.Outputs);
        Assert.True(node.Descriptor.IsTerminal);
        Contracts.Management.Preview.NodePreviewSnapshot snapshot = Assert.Single(store.Read(new("workflow", ["node"])));
        ProgressValue progress = JsonSerializer.Deserialize<ProgressValue>(store.ReadChunk(new(snapshot.Id, 0, snapshot.Length)).Data.Span)!;
        Assert.Equal(percentage, progress.Percentage);
        Assert.Equal(double.Parse(current, System.Globalization.CultureInfo.InvariantCulture), progress.Current);
    }

    /// <summary>不正値は前回の正常進捗を上書きして失敗を表示します。</summary>
    /// <returns>前回の進捗が残らない確認です。</returns>
    [Theory(Skip = "旧仕様: 別のWF実行の不正入力も同じProgressを上書きする。現行は実行単位で分離する。")]
    [InlineData("-1", "0")]
    [InlineData("10", "-0.5")]
    [InlineData("\"10\"", "2")]
    [InlineData("10", "null")]
    [InlineData("1e999", "1")]
    public async Task InvalidInputReplacesPreviousCaptureAsync(string total, string current)
    {
        NodePreviewStore store = new();
        ProgressBarNode node = new(store);
        _ = await node.ExecuteAsync(Context("10", "5")).ConfigureAwait(true);
        NodeResult result = await node.ExecuteAsync(Context(total, current)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Equal("progress.invalid", Assert.Single(store.Read(new("workflow", ["node"]))).Problem);
    }

    /// <summary>同じ実行の不正値は、その実行の正常進捗だけを上書きします。</summary>
    /// <returns>前回の進捗が残らない確認です。</returns>
    [Theory]
    [InlineData("-1", "0")]
    [InlineData("10", "-0.5")]
    [InlineData("\"10\"", "2")]
    [InlineData("10", "null")]
    [InlineData("1e999", "1")]
    public async Task InvalidInputReplacesOnlyTheSameExecutionCaptureAsync(string total, string current)
    {
        NodePreviewStore store = new();
        ProgressBarNode node = new(store);
        NodeExecutionContext first = Context("10", "5");
        _ = await node.ExecuteAsync(first).ConfigureAwait(true);
        NodeResult result = await node.ExecuteAsync(Context(total, current) with { CausationId = first.CausationId }).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Equal("progress.invalid", Assert.Single(store.Read(new("workflow", ["node"]))).Problem);
    }

    /// <summary>値のJSON種類を変換せずに渡します。</summary>
    /// <returns>進捗入力を持つ一回の実行です。</returns>
    private static NodeExecutionContext Context(string total, string current) => NodeTestContext.Create(extra: new Dictionary<string, NodeValue>(StringComparer.Ordinal)
    {
        ["total"] = NodeValue.FromJson(JsonSerializer.Deserialize<JsonElement>(total)),
        ["current"] = NodeValue.FromJson(JsonSerializer.Deserialize<JsonElement>(current)),
    });
}
