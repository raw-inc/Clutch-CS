using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Communication;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>部分実行の値供給だけを補い、副作用の開始範囲を広げないことを検証します。</summary>
public sealed class PureValueInputTests
{
    /// <summary>Source一件/明示Action入口は別根のpureチェーンからmethodを取り、実HTTPを送れます。</summary>
    /// <returns>標準HTTPの実待受とエンジン内の供給結果です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceOrExplicitTargetUsesPureMethodChainWithoutActivatingOtherActions(bool explicitTarget)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        TaskCompletionSource<string> listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        NodeExecutionContext serverInitial = SocketNodeTests.Context(SocketNodeTests.Settings(new { count = 1 })) with
        {
            Inputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["method"] = NodeValue.FromObject("POST") },
        };
        NodeSourceContext serverContext = new(serverInitial, () => serverInitial, (_, _) => ValueTask.CompletedTask, () => false, (status, _) =>
        {
            if (status.State == ESourceState.Listening)
            {
                listening.TrySetResult(status.Message!);
            }
            return ValueTask.CompletedTask;
        });
        Task<NodeResult> server = ((ISourceNode)SocketNodeTests.Find("http.listen")).RunAsync(serverContext, timeout.Token);
        try
        {
            string url = await listening.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
            int unrelatedCalls = 0;
            WorkflowFixture.ActionNode unrelated = new((_, _) =>
            {
                unrelatedCalls++;
                return ValueTask.FromResult(NodeResult.Success());
            });
            WorkflowFixture fixture = new(new HttpMethodNode(), new KeyValueNode(), new SettingsSourceProbe(), SocketNodeTests.Find("http.request"), unrelated);
            await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
            WorkflowDefinition definition = new("pure-http", "pure HTTP",
                [new("method", "http.method", JsonSerializer.SerializeToElement(new { method = "POST" })),
                 new("field", "value.keyValue", JsonSerializer.SerializeToElement(new { key = "verb" })),
                 new("wrapper", "value.keyValue", JsonSerializer.SerializeToElement(new { key = "request" })),
                 new("source", "test.settingSource", JsonSerializer.SerializeToElement(new { port = 1 })),
                 new("send", "http.request", SocketNodeTests.Settings(new { url, method = "GET" }), Target: new(EExecutionScope.One, ["a"])),
                 WorkflowFixture.Definition("unrelated")],
                [new("method", "value", "field", "value"), new("field", "value", "wrapper", "value"),
                 new("wrapper", "value", "send", "method", Path: "/request/verb"), new("source", "value", "send", "value"),
                 new("wrapper", "value", "unrelated", "value")]);
            CompilationResult compiled = fixture.Compiler.Compile(definition);
            Assert.Empty(compiled.Issues);
            CompiledWorkflow plan = compiled.Workflow!;
            Assert.Equal(ContractJson.Hash(definition), plan.Revision);
            ExecutionReport report;
            if (explicitTarget)
            {
                report = await fixture.Engine.ExecuteAsync(plan, "send", cancellationToken: timeout.Token);
            }
            else
            {
                NodeExecutionContext input = new(Guid.NewGuid(), Guid.NewGuid(), definition.Id, plan.Revision, "source", "a", JsonSerializer.SerializeToElement(new { port = 1 }), new Dictionary<string, NodeValue>(StringComparer.Ordinal), TimeProvider.System);
                report = await fixture.Engine.ExecuteEmissionAsync(plan, new(input, NodeResult.Success(NodeValue.FromObject("event"))), timeout.Token);
            }
            Assert.Equal(EOutcome.Succeeded, Assert.Single(report.Nodes, node => string.Equals(node.NodeId, "send", StringComparison.Ordinal)).Outcome);
            Assert.Equal(0, unrelatedCalls);
            Assert.Equal(EOutcome.NotExecuted, Assert.Single(report.Nodes, node => string.Equals(node.NodeId, "method", StringComparison.Ordinal)).Outcome);
            Assert.Equal(EOutcome.Succeeded, (await server.WaitAsync(timeout.Token).ConfigureAwait(true)).Outcome);
        }
        finally
        {
            await timeout.CancelAsync().ConfigureAwait(true);
            await server.ConfigureAwait(true);
        }
    }

    /// <summary>明示入口で実評価した失敗を、以前の成功キャッシュで置き換えません。</summary>
    /// <returns>失敗と未実行の保持です。</returns>
    [Fact]
    public async Task EntryOverrideFailureCannotFallBackToCompiledValue()
    {
        int calls = 0;
        WorkflowFixture fixture = new(new ConstantValueNode(), new WorkflowFixture.ActionNode((_, _) =>
        {
            calls++;
            return ValueTask.FromResult(NodeResult.Success());
        }, true));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = new("override", "override", [new("value", "value.constant", JsonSerializer.SerializeToElement(new { text = "21", type = "integer" })), WorkflowFixture.Definition("sink")], [new("value", "value", "sink", "value")]);
        CompiledWorkflow plan = fixture.Compiler.Compile(definition).Workflow!;
        Dictionary<string, NodeValue> input = new(StringComparer.Ordinal) { ["type"] = NodeValue.FromObject(new ValueTypeDefinition("boolean")) };
        ExecutionReport report = await fixture.Engine.ExecuteAsync(plan, "value", input);
        Assert.Equal(EOutcome.Failed, Assert.Single(report.Nodes, node => string.Equals(node.NodeId, "value", StringComparison.Ordinal)).Outcome);
        Assert.Equal(EOutcome.NotExecuted, Assert.Single(report.Nodes, node => string.Equals(node.NodeId, "sink", StringComparison.Ordinal)).Outcome);
        Assert.Equal(0, calls);
        Assert.Equal(21, plan.ConfigurationValues!["value"].Outputs["value"].Json.GetInt32());
    }

    /// <summary>宣言したpure出力が無いとき、明示Action入口でも既定値で副作用を実行しません。</summary>
    /// <returns>未入力の理由を持つ未実行です。</returns>
    [Theory]
    [InlineData(EOutcome.Succeeded)]
    [InlineData(EOutcome.Failed)]
    [InlineData(EOutcome.Unknown)]
    public async Task MissingPureOutputDoesNotSilentlyUseAnOptionalDefault(EOutcome outcome)
    {
        int calls = 0;
        NodeResult value = NodeResult.Success();
        if (outcome != EOutcome.Succeeded)
        {
            value = NodeResult.Success(NodeValue.FromObject("unconfirmed")) with { Outcome = outcome };
        }
        WorkflowFixture fixture = new(new SettingsValueProbeNode(value), new WorkflowFixture.ActionNode((_, _) =>
        {
            calls++;
            return ValueTask.FromResult(NodeResult.Success());
        }));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = new("missing", "missing", [new("value", "test.value", JsonSerializer.SerializeToElement(new { })), WorkflowFixture.Definition("sink")], [new("value", "value", "sink", "value", "*")]);
        CompiledWorkflow plan = fixture.Compiler.Compile(definition).Workflow!;
        ExecutionReport report = await fixture.Engine.ExecuteAsync(plan, "sink");
        Assert.Equal(EOutcome.NotExecuted, Assert.Single(report.Nodes, node => string.Equals(node.NodeId, "sink", StringComparison.Ordinal)).Outcome);
        Assert.Equal(0, calls);
        CompilationResult cycle = fixture.Compiler.Compile(definition with { Wires = [definition.Wires[0], new("sink", "value", "value", "trigger")] });
        Assert.Contains(cycle.Issues, issue => string.Equals(issue.Code, "wire.cycle", StringComparison.Ordinal));
    }
}
