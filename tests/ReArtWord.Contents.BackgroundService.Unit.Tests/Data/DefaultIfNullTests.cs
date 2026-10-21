using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Data;

/// <summary>nullと未供給を区別し、既定値の選択で型・内容を変換しません。</summary>
public sealed class DefaultIfNullTests
{
    /// <summary>未押下ボタンの既定値は非null時に要求せず、null時には未到着を隠しません。</summary>
    /// <returns>未選択のボタンを押さず、実際に届いた値だけを使用する確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingDefaultOnlyBlocksNullInputsAsync(bool nullInput)
    {
        List<NodeValue> received = [];
        WorkflowFixture fixture = new(new ButtonNode(), new DefaultIfNullNode(), new WorkflowFixture.ActionNode((context, _) =>
        {
            received.Add(context.Inputs["value"]);
            return ValueTask.FromResult(NodeResult.Success());
        }));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = new("default-runtime", "Default runtime", [
            WorkflowFixture.Definition("start", "control.button"), WorkflowFixture.Definition("other", "control.button"),
            WorkflowFixture.Definition("default", "data.defaultIfNull"), WorkflowFixture.Definition("sink")],
            [new("start", "value", "default", "value"), new("other", "value", "default", "defaultValue"), new("default", "value", "sink", "value")]);
        Runtime.Workflows.Compilation.CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        NodeValue input = nullInput ? NodeValue.Null
                                    : NodeValue.FromObject(0);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(compiled.Workflow!, "start", new Dictionary<string, NodeValue> { ["value"] = input }).ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, report.Nodes.Single(node => node.NodeId is "other").Outcome);
        if (nullInput)
        {
            Assert.Empty(received);
            Assert.NotEqual(EOutcome.Succeeded, report.Outcome);
        }
        else
        {
            Assert.Equal(EOutcome.Succeeded, report.Outcome);
            Assert.Equal(0, Assert.Single(received).Json.GetInt32());
        }
    }

    /// <summary>false相当の値や空コンテナーをnullと誤認しません。</summary>
    [Theory]
    [InlineData("null", "42")]
    [InlineData("false", "false")]
    [InlineData("0", "0")]
    [InlineData("\"\"", "\"\"")]
    [InlineData("[]", "[]")]
    [InlineData("{}", "{}")]
    [InlineData("{\"a\":null}", "{\"a\":null}")]
    public void OnlyJsonNullUsesDefault(string input, string expected)
    {
        NodeResult result = Evaluate(NodeValue.FromJson(JsonSerializer.Deserialize<JsonElement>(input)), NodeValue.FromObject(42));
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(expected, result.Outputs["value"].Json.GetRawText());
    }

    /// <summary>既定値のnullは正常値、未供給の主入力はエラーとして区別します。</summary>
    [Fact]
    public void OmittedDefaultAndMissingInputAreDifferent()
    {
        Assert.Equal(JsonValueKind.Null, Evaluate(NodeValue.Null, null).Outputs["value"].Json.ValueKind);
        NodeResult missing = new DefaultIfNullNode().Evaluate(Definition(), new Dictionary<string, NodeValue> { ["defaultValue"] = NodeValue.FromObject(42) });
        Assert.NotEqual(EOutcome.Succeeded, missing.Outcome);
        Assert.Empty(missing.Outputs);
    }

    /// <summary>元入力・既定値のどちらを選んでも、Binaryと明示Path・Int64・辞書型を保持します。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectedValueKeepsBinaryAndTypeMetadata(bool fallback)
    {
        NodeValue[] values = [NodeValue.FromBytes(new byte[] { 0, 255 }), NodeValue.FromBytes(ReadOnlyMemory<byte>.Empty),
            PathTypeMetadata.Apply(NodeValue.FromObject("C:/example.txt"), new("path")),
            PathTypeMetadata.Apply(NodeValue.FromObject(7), new("int64")),
            PathTypeMetadata.Apply(NodeValue.FromObject(new Dictionary<string, int> { ["a"] = 1 }), new("object", KeyType: new("string"), ValueType: new("integer")))];
        foreach (NodeValue value in values)
        {
            NodeResult result = fallback ? Evaluate(NodeValue.Null, value)
                                         : Evaluate(value, NodeValue.FromObject("unused"));
            Assert.Equal(EOutcome.Succeeded, result.Outcome);
            NodeValue selected = result.Outputs["value"];
            Assert.NotSame(value, selected);
            Assert.Equal(value.Kind, selected.Kind);
            Assert.Equal(value.Binary.ToArray(), selected.Binary.ToArray());
            Assert.Equal(value.Json.GetRawText(), selected.Json.GetRawText());
            Assert.True(ValueTypeInference.Same(PathTypeMetadata.TypeOf(value), PathTypeMetadata.TypeOf(selected)));
        }
    }

    /// <summary>固定入力からの型表示も、実際に選択する側と一致させます。</summary>
    [Theory]
    [InlineData("null", "\"fallback\"", "string")]
    [InlineData("false", "42", "boolean")]
    [InlineData("0", "\"fallback\"", "integer")]
    public void InfersTheSelectedLiteralType(string input, string fallback, string expected)
    {
        NodeDefinition node = Definition() with { Inputs = new Dictionary<string, NodeValue>
        {
            ["value"] = NodeValue.FromJson(JsonSerializer.Deserialize<JsonElement>(input)), ["defaultValue"] = NodeValue.FromJson(JsonSerializer.Deserialize<JsonElement>(fallback)),
        } };
        WorkflowDefinition definition = new("default-types", "Default types", [node], []);
        Assert.Equal(expected, new WorkflowTypeInference(definition, [new DefaultIfNullNode().Descriptor]).PortType(node.Id, "value", true)!.Type);
    }

    /// <summary>実ノードの純粋評価を共通の入力形式で呼びます。</summary>
    /// <returns>型変換を経由しないノード結果です。</returns>
    private static NodeResult Evaluate(NodeValue value, NodeValue? fallback)
    {
        Dictionary<string, NodeValue> inputs = new(StringComparer.Ordinal) { ["value"] = value };
        if (fallback is not null)
        {
            inputs["defaultValue"] = fallback;
        }
        return new DefaultIfNullNode().Evaluate(Definition(), inputs);
    }

    /// <summary>追加設定なしで配置できるノードです。</summary>
    /// <returns>最小の保存定義です。</returns>
    private static NodeDefinition Definition() => new("default", "data.defaultIfNull", JsonSerializer.SerializeToElement(new { }));
}
