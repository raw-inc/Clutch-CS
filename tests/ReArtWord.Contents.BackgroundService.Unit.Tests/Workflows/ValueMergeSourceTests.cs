using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>継続待受の寿命を値の到着として数えません。</summary>
public sealed class ValueMergeSourceTests
{
    /// <summary>待受開始・終了は全経路でも除外し、繰り返し届く一件だけを流します。</summary>
    /// <returns>受信回数と同じ後続回数です。</returns>
    [Fact]
    public async Task ListenerLifetimeDoesNotEmitButEveryReceivedPacketDoesAsync()
    {
        int calls = 0;
        List<Guid> events = [];
        ValueMergeFixture.Probe sink = new((context, _) =>
        {
            calls++;
            events.Add(context.CausationId);
            Assert.Equal("packet", context.Inputs["value"].GetString());
            return ValueTask.FromResult(NodeResult.Success());
        });
        WorkflowFixture fixture = new(new Source(), sink, new ValueMergeNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        string receive = "receive";
        NodeResult packet = ValueMergeFixture.Result(NodeValue.FromObject("packet"));
        fixture.Dispatcher.Handler = (request, token) =>
        {
            if (string.Equals(request.Node.Id, receive, StringComparison.Ordinal))
            {
                return Task.FromResult(packet);
            }
            return fixture.Executor.ExecuteAsync(request, token);
        };
        WorkflowDefinition definition = new("source-values", "Source values",
            [WorkflowFixture.Definition(receive, "test.source"), WorkflowFixture.Definition("merge", ValueMergePorts.NodeType), WorkflowFixture.Definition("sink", "test.value")],
            [new(receive, "value", "merge", "input1", "*"), new("merge", "start", "sink", "trigger"), new("merge", "value", "sink", "value")]);
        CompiledWorkflow workflow = ValueMergeFixture.Compile(fixture, definition);
        _ = await fixture.Engine.ExecuteAsync(workflow, receive).ConfigureAwait(true);
        Assert.Equal(0, calls);
        NodeExecutionContext context = new(Guid.NewGuid(), Guid.NewGuid(), definition.Id, workflow.Revision, receive, "a", definition.Nodes[0].Settings,
            new Dictionary<string, NodeValue>(StringComparer.Ordinal), TimeProvider.System);
        _ = await fixture.Engine.ExecuteEmissionAsync(workflow, new(context, packet, true)).ConfigureAwait(true);
        Assert.Equal(0, calls);
        for (int index = 0; index < 3; index++)
        {
            context = context with { ExecutionId = Guid.NewGuid(), CausationId = Guid.NewGuid() };
            Assert.Equal(EOutcome.Succeeded, (await fixture.Engine.ExecuteEmissionAsync(workflow, new(context, packet)).ConfigureAwait(true)).Outcome);
        }
        Assert.Equal(3, calls);
        Assert.Equal(3, events.Distinct().Count());
    }

    /// <summary>待受として登録し、試験では配送応答と一件の通知を個別に供給します。</summary>
    private sealed class Source : ISourceNode
    {
        public NodeDescriptor Descriptor => NodeSupport.Describe("test.source", "Source", "Test", "{}", [], kind: ENodeKind.Source);

        /// <summary>試験固有の制約はありません。</summary>
        /// <returns>問題なしです。</returns>
        public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => [];

        /// <summary>本体を暗黙に再起動した場合は失敗させます。</summary>
        /// <returns>実際には通知経路からのみ値を供給します。</returns>
        public Task<NodeResult> RunAsync(NodeSourceContext context, CancellationToken cancellationToken = default) => throw new InvalidOperationException("The listener must not be restarted by its value consumer.");
    }
}
