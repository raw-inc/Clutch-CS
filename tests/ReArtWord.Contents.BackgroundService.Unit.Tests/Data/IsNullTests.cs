using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Data;

/// <summary>null判定を純粋評価・実行時・入力未到着の境界で検証します。</summary>
public sealed class IsNullTests
{
    /// <summary>空やfalse相当の値を、明示的なnullへ変換しません。</summary>
    [Theory]
    [InlineData("null", true)]
    [InlineData("false", false)]
    [InlineData("true", false)]
    [InlineData("0", false)]
    [InlineData("\"\"", false)]
    [InlineData("\"null\"", false)]
    [InlineData("[]", false)]
    [InlineData("{}", false)]
    [InlineData("[null]", false)]
    [InlineData("{\"value\":null}", false)]
    public void OnlyExplicitNullReturnsTrue(string json, bool expected)
    {
        NodeResult result = new IsNullNode().Evaluate(Definition(), new Dictionary<string, NodeValue>
        {
            ["value"] = NodeValue.FromJson(JsonSerializer.Deserialize<JsonElement>(json)),
        });
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(expected, result.Outputs["value"].Json.GetBoolean());
    }

    /// <summary>空のBinaryも値であり、型付きnullは実際の値に従って判定します。</summary>
    [Fact]
    public void BinaryAndTypedNullRemainDistinct()
    {
        foreach (NodeValue input in new[] { NodeValue.FromBytes(ReadOnlyMemory<byte>.Empty), NodeValue.FromBytes(new byte[] { 0, 255 }),
            PathTypeMetadata.Apply(NodeValue.Null, new("string", Optional: true)) })
        {
            NodeResult result = new IsNullNode().Evaluate(Definition(), new Dictionary<string, NodeValue> { ["value"] = input });
            Assert.Equal(EOutcome.Succeeded, result.Outcome);
            Assert.Equal(input.Kind == EValueKind.Json, result.Outputs["value"].Json.GetBoolean());
        }
        NodeResult missing = new IsNullNode().Evaluate(Definition(), new Dictionary<string, NodeValue>());
        Assert.NotEqual(EOutcome.Succeeded, missing.Outcome);
        Assert.Empty(missing.Outputs);
    }

    /// <summary>各回の実入力を判定し、未押下のボタンからnullを作り出しません。</summary>
    /// <returns>同じノードへ異なる入力を渡した場合と未到着の確認です。</returns>
    [Fact]
    public async Task RuntimeChecksEachInputWithoutInventingMissingValuesAsync()
    {
        List<bool> observed = [];
        WorkflowFixture fixture = new(new ButtonNode(), new IsNullNode(), new WorkflowFixture.ActionNode((context, _) =>
        {
            observed.Add(context.Inputs["value"].Json.GetBoolean());
            return ValueTask.FromResult(NodeResult.Success());
        }));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = new("null-check", "Null check", [WorkflowFixture.Definition("button", "control.button"), Definition(), WorkflowFixture.Definition("sink")],
            [new("button", "value", "check", "value"), new("check", "value", "sink", "value")]);
        CompiledWorkflow compiled = IterationTests.Compile(fixture, definition);
        foreach (NodeValue input in new[] { NodeValue.Null, NodeValue.FromObject(false), NodeValue.Null })
        {
            ExecutionReport report = await fixture.Engine.ExecuteAsync(compiled, "button", new Dictionary<string, NodeValue> { ["value"] = input }).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, report.Outcome);
        }
        bool[] expected = [true, false, true];
        Assert.Equal(expected, observed);
        observed.Clear();
        _ = await fixture.Engine.ExecuteAsync(compiled, "check").ConfigureAwait(true);
        Assert.Empty(observed);
    }

    /// <summary>標準カタログの出力型はJSON一般ではなく真偽値です。</summary>
    [Fact]
    public void CatalogExposesBooleanOutputForAnyInput()
    {
        NodeDescriptor descriptor = Assert.Single(DataNodeRegistration.Create(), node => node.Descriptor.Type is "data.isNull").Descriptor;
        WorkflowDefinition workflow = new("null-type", "Null type", [Definition()], []);
        Assert.Equal(EPortValueKind.Any, Assert.Single(descriptor.Inputs).ValueKind);
        Assert.Equal("boolean", new WorkflowTypeInference(workflow, [descriptor]).PortType("check", "value", true)!.Type);
    }

    /// <summary>設定不要の判定ノードを配置します。</summary>
    /// <returns>最小のノード定義です。</returns>
    private static NodeDefinition Definition() => WorkflowFixture.Definition("check", "data.isNull");
}
