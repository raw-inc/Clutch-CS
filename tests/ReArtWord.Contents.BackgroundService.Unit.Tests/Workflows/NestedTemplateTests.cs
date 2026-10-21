using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>汎用構造作成だけでOSC等の入れ子の型付きメッセージを構成します。</summary>
public sealed class NestedTemplateTests
{
    /// <summary>配列内参照と正常nullを保持し、入力を変更しません。</summary>
    /// <returns>構造と数値精度の確認です。</returns>
    [Fact]
    public async Task NestedObjectAndArrayResolveOnlyDedicatedPointerObjects()
    {
        JsonElement settings = NodeSettings.Schema("""
            {"operation":"create","fields":{"address":"/ready","arguments":[{"type":"s","value":{"$path":"/slotId"}},{"type":"h","value":{"$path":"/number"}},{"value":{"$path":"/missing"}}],"literal":{"$path":"keep","other":true}}}
            """);
        NodeValue input = NodeValue.FromJson(NodeSettings.Schema("""{"slotId":"opening","number":9007199254740993}"""));
        NodeExecutionContext context = new(Guid.NewGuid(), Guid.NewGuid(), "w", "revision", "format", "a", settings,
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = input }, TimeProvider.System, TimeZoneInfo.Utc);

        NodeResult result = await new TransformNode().ExecuteAsync(context).ConfigureAwait(true);

        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        JsonElement value = result.Outputs["value"].Json;
        Assert.Equal("opening", value.GetProperty("arguments")[0].GetProperty("value").GetString());
        Assert.Equal(9007199254740993L, value.GetProperty("arguments")[1].GetProperty("value").GetInt64());
        Assert.Equal(JsonValueKind.Null, value.GetProperty("arguments")[2].GetProperty("value").ValueKind);
        Assert.Equal("keep", value.GetProperty("literal").GetProperty("$path").GetString());
        Assert.Equal(2, input.Json.EnumerateObject().Count());
    }
}
