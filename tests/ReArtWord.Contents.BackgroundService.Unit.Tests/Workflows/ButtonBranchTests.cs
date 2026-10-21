using System.Collections.Concurrent;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Communication.Osc;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.State;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>ボタンから分かれた値とSignalを、複数の合流を通して同じ押下へ束ねます。</summary>
public sealed class ButtonBranchTests
{
    /// <summary>値とSignalを別々に合流しても、押した一件だけを後続へ渡します。</summary>
    /// <returns>パケット・アドレス・Signalを別経路から受け取る一回の送信です。</returns>
    [Theory]
    [InlineData("a", 0)]
    [InlineData("b", 2)]
    public async Task JoinsValuesAndSignalsFromThePressedButtonAsync(string entry, int zone)
    {
        List<NodeExecutionContext> received = [];
        WorkflowFixture fixture = Create((context, _) =>
        {
            received.Add(context);
            return ValueTask.FromResult(NodeResult.Success());
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = Definition();
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(compiled.Workflow!, entry).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        NodeExecutionContext delivered = Assert.Single(received);
        string address = "/zone/" + zone.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/cue/start";
        Assert.Equal(address, delivered.Inputs["address"].GetString());
        Assert.Equal(address, delivered.Inputs["value"].Json.GetProperty("address").GetString());
        Assert.True(delivered.Inputs["trigger"].Json.GetBoolean());
        Assert.Equal(1, delivered.Inputs["value"].Json.GetProperty("arguments").GetArrayLength());
        Assert.Equal(7, delivered.Inputs["value"].Json.GetProperty("arguments")[0].GetProperty("value").GetInt32());
        Assert.Equal(1, report.Nodes.Count(node => node.NodeId is "a" or "b" && node.Outcome == EOutcome.Succeeded));
    }

    /// <summary>別の押下が先に完了しても、二系統で合流したアドレスとパケットを混ぜません。</summary>
    /// <returns>遅い押下と速い押下がそれぞれ一回だけ送信する確認です。</returns>
    [Fact]
    public async Task ConcurrentPressesKeepTheirJoinedValuesAsync()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentQueue<(string Address, string PacketAddress)> delivered = new();
        string firstAddress = "/zone/0/cue/start", addressPort = "address";
        WorkflowFixture fixture = Create(async (context, token) =>
        {
            if (string.Equals(context.Inputs[addressPort].GetString(), firstAddress, StringComparison.Ordinal))
            {
                started.TrySetResult();
                await release.Task.WaitAsync(token).ConfigureAwait(false);
            }
            delivered.Enqueue((context.Inputs["address"].GetString()!, context.Inputs["value"].Json.GetProperty("address").GetString()!));
            return NodeResult.Success();
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        fixture.Engine.RetainedValues = new TestStateStore();
        CompiledWorkflow plan = ValueMergeFixture.Compile(fixture, Definition());
        Task<ExecutionReport> first = fixture.Engine.ExecuteAsync(plan, "a");
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            ExecutionReport second = await fixture.Engine.ExecuteAsync(plan, "b").WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, second.Outcome);
            Assert.False(first.IsCompleted);
            Assert.Equal(("/zone/2/cue/start", "/zone/2/cue/start"), Assert.Single(delivered));
        }
        finally
        {
            release.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
        }
        Assert.Equal(2, delivered.Count);
        Assert.Equal((firstAddress, firstAddress), delivered.Last());
        Assert.Empty(fixture.Engine.Active());
    }

    /// <summary>宛先アドレスの配線不足は、正常な非選択と混同せず失敗として残します。</summary>
    /// <returns>不正パケットを送信しない確認です。</returns>
    [Fact]
    public async Task MissingPacketAddressStillFailsWithoutSendingAsync()
    {
        int calls = 0;
        WorkflowFixture fixture = Create((_, _) =>
        {
            calls++;
            return ValueTask.FromResult(NodeResult.Success());
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = Definition();
        definition = definition with { Wires = definition.Wires.Where(wire => !(wire.ToNode is "packet" && wire.ToPort is "address")).ToArray() };
        ExecutionReport report = await fixture.Engine.ExecuteAsync(ValueMergeFixture.Compile(fixture, definition), "a").WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, report.Outcome);
        NodeExecutionReport packet = Assert.Single(report.Nodes, node => node.NodeId is "packet");
        Assert.Equal("value.osc.address.required", Assert.Single(packet.Targets).Result.ReasonCode);
        Assert.Equal(0, calls);
    }

    /// <summary>通常の値構築とSignalを使い、送信の境界だけを観測に置き換えます。</summary>
    /// <returns>外部通信なしで実グラフを動かす構成です。</returns>
    private static WorkflowFixture Create(Func<NodeExecutionContext, CancellationToken, ValueTask<NodeResult>> receive) => new(new ButtonNode(), new ValueMergeNode(), new SignalMergeNode("first", new TestStateStore()),
        new ConstantValueNode(), new ConvertNode(), new ConcatNode(), new OscPacketNode(), new ValueMergeFixture.Probe(receive));

    /// <summary>運用の配線と同じ、ボタン→値合流→加工→二系統合流と別経路のSignalです。</summary>
    /// <returns>外部通信を行わない送信入力の検証用グラフです。</returns>
    private static WorkflowDefinition Definition() => new("button-branches", "Button branches",
        [Button("a", 0), Button("b", 2), Node("zones", ValueMergePorts.NodeType), Node("signal", "control.signal.first"),
            Node("convert", "data.convert", """{"targetType":"string"}"""), Node("prefix", "value.constant", """{"text":"/zone/"}"""),
            Node("suffix", "value.constant", """{"text":"/cue/start"}"""), Node("address", "data.concat"),
            Node("packet", "osc.packet") with { Inputs = new Dictionary<string, NodeValue> { ["input1"] = NodeValue.FromObject(7) } },
            Node("addresses", ValueMergePorts.NodeType), Node("packets", ValueMergePorts.NodeType), Node("sink", "test.value")],
        [new("a", "value", "zones", "input1"), new("b", "value", "zones", "input2"),
            new("a", "start", "signal", "input1"), new("b", "start", "signal", "input2"),
            new("zones", "value", "convert", "value"), new("prefix", "value", "address", "input1"), new("convert", "value", "address", "input2"), new("suffix", "value", "address", "input3"),
            new("address", "value", "packet", "address"), new("address", "value", "packet", "trigger"),
            new("address", "value", "addresses", "input1"), new("packet", "value", "packets", "input1"),
            new("addresses", "value", "sink", "address"), new("packets", "value", "sink", "value"), new("signal", "start", "sink", "trigger")]);

    /// <summary>番号を持つ独立した手動入口です。</summary>
    /// <returns>押下だけが値とSignalを出すノードです。</returns>
    private static NodeDefinition Button(string id, int zone) => Node(id, ButtonValuePorts.NodeType) with
    {
        Inputs = new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(zone) },
    };

    /// <summary>標準の設定形式を共有します。</summary>
    /// <returns>検証用の保存定義です。</returns>
    private static NodeDefinition Node(string id, string type, string settings = "{}") => WorkflowFixture.Definition(id, type, settings);
}
