using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>明示押下だけを開始事象とし、型付きの値と並列な後続実行を保ちます。</summary>
public sealed class ButtonNodeTests
{
    /// <summary>文字列・数値・構造値・バイナリ・nullを、押下一回につき一回渡します。</summary>
    /// <returns>実エンジンから後続へ届く入力の確認です。</returns>
    [Theory]
    [InlineData("text")]
    [InlineData("number")]
    [InlineData("object")]
    [InlineData("binary")]
    [InlineData("null")]
    public async Task ExplicitPressPreservesValueAndEmitsOneSignal(string kind)
    {
        NodeValue value = kind switch
        {
            "text" => NodeValue.FromObject("hello"),
            "number" => NodeValue.FromObject(42),
            "object" => NodeValue.FromObject(new { stub = true }),
            "binary" => NodeValue.FromBytes(new byte[] { 0, 1, 255 }),
            _ => NodeValue.Null,
        };
        List<NodeExecutionContext> calls = [];
        WorkflowFixture fixture = new(new ButtonNode(), new WorkflowFixture.ActionNode((context, _) =>
        {
            calls.Add(context);
            return ValueTask.FromResult(NodeResult.Success());
        }, required: true));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = Definition(value);
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(compiled.Workflow!, "button").ConfigureAwait(true);
        NodeExecutionContext call = Assert.Single(calls);
        Assert.True(call.Inputs["trigger"].Json.GetBoolean());
        Assert.Equal(value.Kind, call.Inputs["value"].Kind);
        if (value.Kind == EValueKind.Binary)
        {
            Assert.Equal(value.Binary.ToArray(), call.Inputs["value"].Binary.ToArray());
        }
        else
        {
            Assert.Equal(value.Json.GetRawText(), call.Inputs["value"].Json.GetRawText());
        }
        Assert.Equal(EOutcome.Succeeded, report.Nodes.Single(node => node.NodeId is "target").Outcome);
        WorkflowTypeInference types = new(definition, fixture.Registry.Descriptors);
        Assert.Equal("boolean", types.PortType("button", "start", true)!.Type);
        Assert.Equal(types.PortType("button", "value", false), types.PortType("button", "value", true));
    }

    /// <summary>全体実行・他の開始信号・値の要求だけではボタンを押しません。</summary>
    /// <returns>実行入口ごとの後続抑止です。</returns>
    [Theory]
    [InlineData(null)]
    [InlineData("gate")]
    [InlineData("target")]
    public async Task AutomaticInputsAndDemandDoNotPressTheButton(string? entry)
    {
        int calls = 0;
        WorkflowFixture fixture = new(new ButtonNode(), new IfNode(), new WorkflowFixture.ActionNode((_, _) =>
        {
            calls++;
            return ValueTask.FromResult(NodeResult.Success());
        }, required: true));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = Definition(NodeValue.FromObject("payload"));
        NodeDefinition gate = WorkflowFixture.Definition("gate", "control.ifStart") with { Inputs = new Dictionary<string, NodeValue> { ["condition"] = NodeValue.FromObject(true) } };
        definition = definition with { Nodes = [gate, .. definition.Nodes], Wires = [new("gate", "start", "button", "trigger"), .. definition.Wires] };
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(compiled.Workflow!, entry).ConfigureAwait(true);
        Assert.Equal(0, calls);
        NodeResult result = Assert.Single(report.Nodes.Single(node => node.NodeId is "button").Targets).Result;
        Assert.Equal("button.notPressed", result.ReasonCode);
        Assert.Empty(result.Outputs);
    }

    /// <summary>配線の入力値を押下時に評価し、直接入力の有無に依存しません。</summary>
    /// <returns>接続値と表示文字列の型検証です。</returns>
    [Fact]
    public async Task PressResolvesConnectedValues()
    {
        NodeValue? received = null;
        WorkflowFixture fixture = new(new ButtonNode(), new ConstantValueNode(), new WorkflowFixture.ActionNode((context, _) =>
        {
            received = context.Inputs["value"];
            return ValueTask.FromResult(NodeResult.Success());
        }));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = Definition(NodeValue.Null);
        NodeDefinition constant = WorkflowFixture.Definition("constant", "value.constant", """{"type":"string","text":"connected"}""");
        definition = definition with { Nodes = [constant, definition.Nodes[0] with { Inputs = null }, definition.Nodes[1]],
            Wires = [new("constant", "value", "button", "value"), new("constant", "value", "button", "label"), .. definition.Wires] };
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        _ = await fixture.Engine.ExecuteAsync(compiled.Workflow!, "button").ConfigureAwait(true);
        Assert.Equal("connected", received!.GetString());
    }

    /// <summary>先の押下の後続が待機中でも、別の押下を受け付けます。</summary>
    /// <returns>二つの実行が独立した事象として進む確認です。</returns>
    [Fact]
    public async Task AnotherPressCanRunWhilePreviousDownstreamWaits()
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        WorkflowFixture fixture = new(new ButtonNode(), new WorkflowFixture.ActionNode(async (_, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.SetResult();
                await release.Task.WaitAsync(token).ConfigureAwait(false);
            }
            return NodeResult.Success();
        }));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        CompiledWorkflow plan = fixture.Compiler.Compile(Definition(NodeValue.FromObject(1))).Workflow!;
        Task<ExecutionReport> first = fixture.Engine.ExecuteAsync(plan, "button");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            ExecutionReport second = await fixture.Engine.ExecuteAsync(plan, "button").WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, second.Outcome);
            Assert.False(first.IsCompleted);
            Assert.Equal(2, calls);
        }
        finally
        {
            release.TrySetResult();
            await first.ConfigureAwait(true);
        }
    }

    /// <summary>型を保った値と開始信号を同じ後続へ接続します。</summary>
    /// <returns>ボタンの標準的な利用形です。</returns>
    private static WorkflowDefinition Definition(NodeValue value) => new("button-test", "Button test",
        [WorkflowFixture.Definition("button", "control.button") with { Inputs = new Dictionary<string, NodeValue> { ["value"] = value, ["label"] = NodeValue.FromObject("Go") } },
         WorkflowFixture.Definition("target")],
        [new("button", "start", "target", "trigger"), new("button", "value", "target", "value")]);
}
