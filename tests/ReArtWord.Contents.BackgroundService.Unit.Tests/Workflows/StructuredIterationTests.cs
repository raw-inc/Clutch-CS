using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>任意項目の反復出力と、明示型の反復前検証を確認します。</summary>
public sealed class StructuredIterationTests
{
    private static string Data => """[{"item-id":1,"quantity":10,"name":"ケーブル"},{"item-id":2,"quantity":20,"name":"アダプター"}]""";
    private static string Manual => """{"type":"object","properties":{"item-id":{"type":"integer"},"quantity":{"type":"integer"},"name":{"type":"string"}}}""";
    private static string Schema => """{"type":"object","properties":{"item-id":{"type":"integer"},"quantity":{"type":"integer","minimum":0,"maximum":120},"name":{"type":"string","minLength":1}},"required":["item-id","quantity","name"],"additionalProperties":false}""";

    /// <summary>逐次と並列の各モードで任意キーを個別出力から取り出せます。</summary>
    /// <returns>入力順の集約とitem全体の互換性です。</returns>
    [Theory]
    [InlineData("auto", false)]
    [InlineData("auto", true)]
    [InlineData("manual", false)]
    [InlineData("manual", true)]
    [InlineData("schema", false)]
    [InlineData("schema", true)]
    public async Task ObjectPropertiesRunAsIndividualIterationOutputs(string mode, bool parallel)
    {
        WorkflowFixture fixture = IterationTests.Create((context, _) => ValueTask.FromResult(NodeResult.Success(context.Inputs["value"])));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = Definition(Data, mode, parallel, "item-id");
        ExecutionReport report = await fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, definition)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        Assert.Equal(2, IterationTests.Loop(report).Iteration!.Started);
        Assert.Equal("[1,2]", Assert.Single(report.Nodes[^1].Targets).Result.Outputs["value"].Json.GetRawText());
    }

    /// <summary>後半だけが不一致でも、最初の要素の副作用を開始しません。</summary>
    /// <returns>bodyが一度も呼ばれないことの確認です。</returns>
    [Theory]
    [InlineData("manual", false)]
    [InlineData("schema", false)]
    [InlineData("manual", true)]
    [InlineData("schema", true)]
    public async Task InvalidLaterElementPreventsEveryBody(string mode, bool parallel)
    {
        int called = 0;
        WorkflowFixture fixture = IterationTests.Create((_, _) =>
        {
            Interlocked.Increment(ref called);
            return ValueTask.FromResult(NodeResult.Success());
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        string data = """[{"item-id":1,"quantity":10,"name":"valid"},{"item-id":2,"quantity":"wrong","name":"invalid"}]""";
        ExecutionReport report = await fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, Definition(data, mode, parallel, "item-id"))).ConfigureAwait(true);
        Assert.NotEqual(EOutcome.Succeeded, report.Outcome);
        Assert.Equal(0, called);
        Assert.Equal("iteration.elementType", Assert.Single(IterationTests.Loop(report).Targets).Result.ReasonCode);
    }

    /// <summary>型が同じでもスキーマの数値範囲・文字列制約を省略しません。</summary>
    /// <returns>JSON Schemaの型以外の制約確認です。</returns>
    [Theory]
    [InlineData("[{\"item-id\":1,\"quantity\":200,\"name\":\"ケーブル\"}]")]
    [InlineData("[{\"item-id\":1,\"quantity\":10,\"name\":\"\"}]")]
    [InlineData("[{\"item-id\":1,\"quantity\":10}]")]
    [InlineData("[{\"item-id\":1,\"quantity\":10,\"name\":\"ケーブル\",\"extra\":true}]")]
    public async Task SchemaConstraintsAreEnforced(string data)
    {
        Nodes.Control.Iteration.ForEachNode node = new();
        NodeDefinition definition = Definition(data, "schema", false, "item-id").Nodes[0];
        NodeResult result = await node.ExecuteAsync(NodeTestContext.Create(definition.Settings.GetRawText(), NodeValue.FromJson(JsonSerializer.Deserialize<JsonElement>(data))));
        Assert.Equal(EOutcome.NotExecuted, result.Outcome);
        Assert.Empty(result.Outputs);
    }

    /// <summary>予約名・ハイフン・区切り記号・日本語を別々のキーとして維持します。</summary>
    /// <returns>エスケープ後も同じ項目を参照する確認です。</returns>
    [Theory]
    [InlineData("index")]
    [InlineData("item")]
    [InlineData("item-id")]
    [InlineData("a/b")]
    [InlineData("a~b")]
    [InlineData("名前")]
    [InlineData("")]
    public async Task PropertyNamesCannotCollideWithControlPorts(string property)
    {
        string data = JsonSerializer.Serialize(new[] { new Dictionary<string, int>(StringComparer.Ordinal) { [property] = 42 } });
        WorkflowFixture fixture = IterationTests.Create((context, _) => ValueTask.FromResult(NodeResult.Success(context.Inputs["value"])));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, Definition(data, "auto", false, property))).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        Assert.Equal("[42]", Assert.Single(report.Nodes[^1].Targets).Result.Outputs["value"].Json.GetRawText());
    }

    /// <summary>配列作成でも同じJSON Schemaを全要素へ検証します。</summary>
    /// <returns>関連する値ノードの入力契約確認です。</returns>
    [Fact]
    public async Task ArrayBuilderSharesSchemaValidation()
    {
        string settings = JsonSerializer.Serialize(new { elementMode = "schema", elementSchema = JsonSerializer.Deserialize<JsonElement>(Schema) });
        Dictionary<string, NodeValue> inputs = new(StringComparer.Ordinal) { ["items"] = NodeValue.FromJson(JsonSerializer.Deserialize<JsonElement>(Data)) };
        Assert.Equal(EOutcome.Succeeded, (await new ArrayValueNode().ExecuteAsync(NodeTestContext.Create(settings, extra: inputs))).Outcome);
        inputs["item"] = NodeValue.FromObject(new { quantity = 10 });
        Assert.Equal(EOutcome.Failed, (await new ArrayValueNode().ExecuteAsync(NodeTestContext.Create(settings, extra: inputs))).Outcome);
    }

    /// <summary>ローカル参照は検証し、外部参照を取得しません。</summary>
    /// <returns>自己完結スキーマと不明な参照の境界です。</returns>
    [Fact]
    public async Task LocalSchemaReferencesWorkAndUnresolvedReferencesFail()
    {
        JsonElement schema = JsonSerializer.Deserialize<JsonElement>("""{"$defs":{"item":{"type":"integer","minimum":1}},"$ref":"#/$defs/item"}""");
        string settings = JsonSerializer.Serialize(new { elementMode = "schema", elementSchema = schema });
        NodeResult good = await new Nodes.Control.Iteration.ForEachNode().ExecuteAsync(NodeTestContext.Create(settings, NodeValue.FromObject(Enumerable.Range(1, 2))));
        Assert.Equal(EOutcome.Succeeded, good.Outcome);
        JsonElement external = JsonSerializer.Deserialize<JsonElement>("""{"$ref":"https://example.invalid/schema"}""");
        string invalid = JsonSerializer.Serialize(new { elementMode = "schema", elementSchema = external });
        NodeResult failed = await new Nodes.Control.Iteration.ForEachNode().ExecuteAsync(NodeTestContext.Create(invalid, NodeValue.FromObject(Enumerable.Range(1, 1))));
        Assert.NotEqual(EOutcome.Succeeded, failed.Outcome);
    }

    /// <summary>一つの項目だけをbodyへ接続した反復を構成します。</summary>
    /// <returns>同じ実行境界を通る設定です。</returns>
    private static WorkflowDefinition Definition(string data, string mode, bool parallel, string property)
    {
        WorkflowDefinition definition = IterationTests.Definition(data, parallel);
        JsonElement settings = JsonSerializer.SerializeToElement(new
        {
            elementMode = mode,
            elementType = JsonSerializer.Deserialize<JsonElement>(Manual),
            elementSchema = JsonSerializer.Deserialize<JsonElement>(Schema),
        });
        NodeDefinition start = definition.Nodes[0] with { Settings = settings };
        string itemPort = "item";
        return definition with
        {
            Nodes = [start, .. definition.Nodes.Skip(1)],
            Wires = definition.Wires.Select(wire => string.Equals(wire.FromPort, itemPort, StringComparison.Ordinal) ? wire with { FromPort = PropertyPorts.Name("item", property) }
                                                                                                                     : wire).ToArray(),
        };
    }
}
