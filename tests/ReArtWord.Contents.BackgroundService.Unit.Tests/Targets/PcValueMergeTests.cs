using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Targets;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Targets;

/// <summary>PC候補のValueMergeは一入力を採用し、通常の値イベントと混合しません。</summary>
public sealed class PcValueMergeTests
{
    /// <summary>同時に確定した先頭入力だけを実行計画へ固定し、後続は一回だけ動きます。</summary>
    /// <returns>高優先度の別候補を混ぜず、開始信号を生成しない確認です。</returns>
    [Fact]
    public async Task StaticCandidatesChooseFirstInputWithoutDuplicateExecutionAsync()
    {
        WorkflowFixture fixture = new(new PcSelectionNode(), new CommentNode(), new ValueMergeNode(), new LowestResourcePcNode(),
            new WorkflowFixture.ActionNode((_, _) => ValueTask.FromResult(NodeResult.Success())));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        fixture.Peers.Others = [new("z", Guid.NewGuid(), "host-z", "127.0.0.1", 1, EPeerStatus.Ready, 900, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)];
        List<RemoteExecutionRequest> requests = [];
        fixture.Dispatcher.Handler = (request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(NodeResult.Success());
        };
        WorkflowDefinition document = Definition();
        CompilationResult compiled = fixture.Compiler.Compile(document);
        Assert.Empty(compiled.Issues);
        Assert.NotNull(compiled.Workflow);
        Assert.Equal("host-a", Assert.Single(Assert.Single(compiled.Workflow.PcSelections!["run"]).HostIds!));
        Assert.Single(compiled.Workflow.ConfigurationValues!["merge"].Outputs);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(compiled.Workflow).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        Assert.Equal("a", Assert.Single(requests).TargetPeerId);
        Assert.Equal("selection.configuration", report.Nodes.Single(node => node.NodeId is "merge").Targets.Single().Result.ReasonCode);
    }

    /// <summary>PC候補をJSONへ暗黙変換せず、通常値との混在や抽出も拒否します。</summary>
    [Fact]
    public void PcCandidatesKeepTheirPortBoundaryThroughValueMerge()
    {
        WorkflowDefinition document = Definition();
        NodeDescriptor[] catalog = [new PcSelectionNode().Descriptor, new ValueMergeNode().Descriptor, new CommentNode().Descriptor, new ConstantValueNode().Descriptor,
            new WorkflowFixture.ActionNode((_, _) => ValueTask.FromResult(NodeResult.Success())).Descriptor];
        Assert.All(document.Wires, wire => Assert.True(WorkflowConnectionTypes.CanConnect(document, catalog, wire)));
        Assert.Equal(PcSelectionPorts.TypeName, new WorkflowTypeInference(document, catalog).PortType("merge", "value", true)!.Type);
        Assert.Equal("first", PcSelectionPorts.Origin(document, document.Wires[^1])!.Id);
        Assert.False(WorkflowConnectionTypes.CanConnect(document, catalog, new("merge", "value", "run", "value")));
        Assert.False(WorkflowConnectionTypes.CanConnect(document, catalog, new("first", "value", "merge", "input3", Path: "/0")));
        document = document with { Nodes = [.. document.Nodes, new("json", "value.constant", NodeSettings.Schema("{\"text\":\"12\"}"))] };
        Assert.False(WorkflowConnectionTypes.CanConnect(document, catalog, new("json", "value", "merge", "input3")));
    }

    /// <summary>入力番号と定義内の記載順を意図的に変えたPC選択グラフです。</summary>
    /// <returns>Commentも経由するPC候補の合流です。</returns>
    private static WorkflowDefinition Definition() => new("pc-merge", "PC merge", [
        new("second", PcSelectionPorts.SelectNode, NodeSettings.Schema("""{"mode":"selected","hostIds":["host-z"]}""")),
        new("first", PcSelectionPorts.SelectNode, NodeSettings.Schema("""{"mode":"selected","hostIds":["host-a"]}""")),
        new("comment", "value.comment", NodeSettings.Schema("{}")), new("merge", ValueMergePorts.NodeType, NodeSettings.Schema("{}")),
        new("run", "test.action", NodeSettings.Schema("{}"))],
        [new("second", "value", "merge", "input2"), new("first", "value", "comment", "value"), new("comment", "value", "merge", "input1"), new("merge", "value", "run", PcSelectionPorts.Execution)]);
}
