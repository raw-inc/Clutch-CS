using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.State;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.State;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.State;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>信号での反転と、スタブ・本番値の副作用のない参照を区別します。</summary>
public sealed class ToggleButtonTests
{
    /// <summary>手動と配線から反転でき、信号入力が接続済みでも現在値を参照できます。</summary>
    /// <returns>共有状態と、変化時だけの後続起動を実エンジンで確認します。</returns>
    [Fact]
    public async Task ManualAndSignalChangesSelectValuesWhileReadsDoNotToggle()
    {
        TestStateStore store = new();
        List<string?> changes = [], reads = [];
        WorkflowFixture fixture = new(new ButtonNode(), new ToggleButtonNode(store), new WorkflowFixture.ActionNode((context, _) =>
        {
            string changedNode = "changed";
            List<string?> received = reads;
            if (string.Equals(context.NodeId, changedNode, StringComparison.Ordinal))
            {
                received = changes;
            }
            received.Add(context.Inputs["value"].GetString());
            return ValueTask.FromResult(NodeResult.Success(context.Inputs["value"]));
        }, required: true));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        CompilationResult compiled = fixture.Compiler.Compile(Definition());
        Assert.Empty(compiled.Issues);
        _ = await fixture.Engine.ExecuteAsync(compiled.Workflow!, "read").ConfigureAwait(true);
        Assert.Equal(["stub"], reads);
        Assert.Empty(changes);
        Assert.Null(await store.ReadAsync(NodeStateScope.Shared("toggle-test"), ToggleButtonState.Key("toggle")).ConfigureAwait(true));
        _ = await fixture.Engine.ExecuteAsync(compiled.Workflow!, "toggle", new Dictionary<string, NodeValue> { ["trigger"] = NodeValue.FromObject(true) }).ConfigureAwait(true);
        Assert.Equal(["production"], changes);
        _ = await fixture.Engine.ExecuteAsync(compiled.Workflow!, "read").ConfigureAwait(true);
        Assert.Equal("production", reads[^1]);
        Assert.Single(changes);
        _ = await fixture.Engine.ExecuteAsync(compiled.Workflow!, "button").ConfigureAwait(true);
        Assert.Equal(["production", "stub"], changes);
        _ = await fixture.Engine.ExecuteAsync(compiled.Workflow!, "read").ConfigureAwait(true);
        Assert.Equal("stub", reads[^1]);
        Assert.Equal(2, changes.Count);
        Assert.True(ToggleButtonState.TryRead((await store.ReadAsync(NodeStateScope.Shared("toggle-test"), ToggleButtonState.Key("toggle")).ConfigureAwait(true))!.Value, out ToggleButtonState state));
        Assert.False(state.IsOn);
        Assert.Equal(2, state.Events.Count);
    }

    /// <summary>同じ入力を重ねず、別の実装インスタンスと定義版でも現在状態を維持します。</summary>
    /// <returns>一事象の重複抑止と参照時の不変性です。</returns>
    [Fact]
    public async Task DuplicateAndReadDoNotChangeStateAcrossInstancesOrRevisions()
    {
        TestStateStore store = new();
        NodeExecutionContext press = Context();
        NodeResult first = await new ToggleButtonNode(store).ExecuteAsync(press).ConfigureAwait(true);
        Assert.True(first.Outputs["isOn"].Json.GetBoolean());
        Assert.True(first.Outputs["start"].Json.GetBoolean());
        NodeResult duplicate = await new ToggleButtonNode(store).ExecuteAsync(press with { ExecutionId = Guid.NewGuid() }).ConfigureAwait(true);
        Assert.True(duplicate.Outputs["isOn"].Json.GetBoolean());
        Assert.False(duplicate.Outputs.ContainsKey("start"));
        NodeResult read = await new ToggleButtonNode(store).ExecuteAsync(press with { CausationId = Guid.NewGuid(), IsValueRead = true, Revision = "updated" }).ConfigureAwait(true);
        Assert.True(read.Outputs["isOn"].Json.GetBoolean());
        Assert.Equal("production", read.Outputs["value"].GetString());
        Assert.False(read.Outputs.ContainsKey("start"));
    }

    /// <summary>保存できたか不明な切替では、確定Signalを発火しません。</summary>
    /// <returns>確認不能な副作用を再実行しない結果です。</returns>
    [Fact]
    public async Task UnconfirmedStateWriteDoesNotEmitSignal()
    {
        UnconfirmedStore store = new();
        NodeResult result = await new ToggleButtonNode(store).ExecuteAsync(Context()).ConfigureAwait(true);
        Assert.Equal(EOutcome.Unknown, result.Outcome);
        Assert.Equal("toggle.stateUnconfirmed", result.ReasonCode);
        Assert.Empty(result.Outputs);
        Assert.Equal(1, store.Writes);
    }

    /// <summary>保存された破損値を初期OFFとして上書きしません。</summary>
    /// <returns>不正状態を明示し、既存の保存値を保持します。</returns>
    [Fact]
    public async Task CorruptStateIsNotReset()
    {
        TestStateStore store = new();
        NodeValue corrupt = NodeValue.FromObject(new { unknown = true });
        string scope = NodeStateScope.Shared("toggle-test"), key = ToggleButtonState.Key("toggle");
        _ = await store.CompareExchangeAsync(scope, key, null, corrupt).ConfigureAwait(true);
        NodeResult result = await new ToggleButtonNode(store).ExecuteAsync(Context()).ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, result.Outcome);
        Assert.Equal("toggle.stateInvalid", result.ReasonCode);
        Assert.Equal(corrupt.Json.GetRawText(), (await store.ReadAsync(scope, key).ConfigureAwait(true))!.Value.Json.GetRawText());
    }

    /// <summary>共有状態を一件につき一回更新し、履歴は有限に保ちます。</summary>
    /// <returns>反転回数と保持量の確認です。</returns>
    [Fact]
    public async Task ConcurrentChangesRemainAtomicAndHistoryIsBounded()
    {
        TestStateStore store = new();
        ToggleButtonNode node = new(store);
        List<NodeResult> results = [];
        foreach (int[] batch in Enumerable.Range(0, 130).Chunk(4))
        {
            results.AddRange(await Task.WhenAll(batch.Select(async _ =>
            {
                await Task.Yield();
                return await node.ExecuteAsync(Context()).ConfigureAwait(false);
            })).ConfigureAwait(true));
        }
        Assert.All(results, result => Assert.Equal(EOutcome.Succeeded, result.Outcome));
        Assert.All(results, result => Assert.True(result.Outputs["start"].Json.GetBoolean()));
        VersionedState? saved = await store.ReadAsync(NodeStateScope.Shared("toggle-test"), ToggleButtonState.Key("toggle")).ConfigureAwait(true);
        Assert.True(ToggleButtonState.TryRead(saved!.Value, out ToggleButtonState state));
        Assert.False(state.IsOn);
        Assert.Equal(128, state.Events.Count);
    }

    /// <summary>同じ候補型は引き継ぎ、切替で型が変わるときはanyとします。</summary>
    [Theory]
    [InlineData(false, "string")]
    [InlineData(true, "any")]
    public void OutputTypesDoNotDependOnCurrentState(bool mixed, string expected)
    {
        WorkflowDefinition definition = Definition();
        if (mixed)
        {
            NodeDefinition toggle = definition.Nodes[1] with { Inputs = new Dictionary<string, NodeValue> { ["offValue"] = NodeValue.FromObject(1), ["onValue"] = NodeValue.FromObject("live") } };
            definition = definition with { Nodes = [definition.Nodes[0], toggle, .. definition.Nodes.Skip(2)] };
        }
        WorkflowTypeInference types = new(definition, [new ToggleButtonNode(new TestStateStore()).Descriptor]);
        Assert.Equal(expected, types.PortType("toggle", "value", true)!.Type);
        Assert.Equal("boolean", types.PortType("toggle", "isOn", true)!.Type);
        Assert.Equal("boolean", types.PortType("toggle", "start", true)!.Type);
    }

    /// <summary>単一の押下文脈を構築します。</summary>
    /// <returns>ONへ切り替える信号付き入力です。</returns>
    private static NodeExecutionContext Context() => new(Guid.NewGuid(), Guid.NewGuid(), "toggle-test", "base", "toggle", "a", NodeValue.FromObject(new { }).Json,
        new Dictionary<string, NodeValue> { ["offValue"] = NodeValue.FromObject("stub"), ["onValue"] = NodeValue.FromObject("production"), ["trigger"] = NodeValue.FromObject(true) }, TimeProvider.System);

    /// <summary>外部Signalと値参照を別々に接続します。</summary>
    /// <returns>スタブと本番の切替に使う実グラフです。</returns>
    private static WorkflowDefinition Definition() => new("toggle-test", "Toggle test",
        [WorkflowFixture.Definition("button", "control.button"),
         WorkflowFixture.Definition("toggle", "control.toggleButton") with { Inputs = new Dictionary<string, NodeValue> { ["offValue"] = NodeValue.FromObject("stub"), ["onValue"] = NodeValue.FromObject("production") } },
         WorkflowFixture.Definition("read"), WorkflowFixture.Definition("changed")],
        [new("button", "start", "toggle", "trigger"), new("toggle", "value", "read", "value"), new("toggle", "start", "changed", "trigger"), new("toggle", "value", "changed", "value")]);

    /// <summary>一回の比較保存の確認だけを失わせます。</summary>
    private sealed class UnconfirmedStore : INodeStateStore
    {
        internal int Writes
        {
            get; private set;
        }

        /// <summary>初回の状態を返します。</summary>
        /// <returns>未保存です。</returns>
        public ValueTask<VersionedState?> ReadAsync(string scope, string key, CancellationToken cancellationToken = default) => ValueTask.FromResult<VersionedState?>(null);

        /// <summary>適用可否が確認できない書込みを返します。</summary>
        /// <returns>再試行できない保存結果です。</returns>
        public ValueTask<StateWriteResult> CompareExchangeAsync(string scope, string key, string? expectedVersion, NodeValue? value, CancellationToken cancellationToken = default)
        {
            Writes++;
            return ValueTask.FromResult(new StateWriteResult(false, null, EOutcome.Unknown));
        }
    }
}
