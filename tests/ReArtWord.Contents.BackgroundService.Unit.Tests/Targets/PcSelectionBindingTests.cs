using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Scheduling;
using ReArtWord.Contents.BackgroundService.Nodes.Targets;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Targets;

/// <summary>PC選択の配線を実行計画へ接続し、通常の入力との境界を確認します。</summary>
public sealed class PcSelectionBindingTests
{
    /// <summary>論理名だけの変更は同じPC選択版を保持し、割当を選び直しません。</summary>
    /// <returns>実コンパイルによる選択版の比較です。</returns>
    [Fact]
    public async Task LogicalNamesDoNotChangePcSelectionRevisionsAsync()
    {
        await using WorkflowFixture fixture = Fixture();
        WorkflowDefinition original = Definition();
        CompilationResult before = fixture.Compiler.Compile(original);
        CompilationResult after = fixture.Compiler.Compile(original with { Nodes = original.Nodes.Select(node => node with { LogicalName = "同じ表示名" }).ToArray() });
        Assert.Empty(before.Issues);
        Assert.Empty(after.Issues);
        Assert.NotEmpty(before.Workflow!.PcSelectionRevisions!);
        Assert.Equal(before.Workflow.PcSelectionRevisions!.OrderBy(pair => pair.Key, StringComparer.Ordinal), after.Workflow!.PcSelectionRevisions!.OrderBy(pair => pair.Key, StringComparer.Ordinal));
    }

    /// <summary>協調可能なDegradedでも、無関係な待受を開始できます。</summary>
    /// <returns>実Sourceの開始確認。</returns>
    [Fact]
    public async Task UnrelatedSourceStartsWhilePeerIsDegradedAsync()
    {
        WorkflowFixture fixture = new(new ScheduleNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(false);
        fixture.Peers.Status = EPeerStatus.Degraded;
        WorkflowDefinition definition = new("degraded", "Continue source", [new("timer", "scheduling.timer",
            NodeSettings.Schema("""{"mode":"once","at":"2020-01-01T00:00:00Z"}"""), StartOnLoad: true)], [], Enabled: true);
        await fixture.Sources.ApplyAsync(fixture.Compiler.Compile(definition).Workflow!);
        Assert.Single(fixture.Sources.GetSources());
    }

    /// <summary>複数のPC選択から最優先の一台へ配送し、選択ノード自体は遠隔実行しません。</summary>
    /// <returns>実行要求と結果の確認完了。</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task MultipleSelectionWiresDispatchOnceToHighestPriorityAsync(int comments)
    {
        await using WorkflowFixture fixture = Fixture();
        PeerInfo other = new("z", Guid.NewGuid(), "host-z", "127.0.0.1", 1, EPeerStatus.Ready, 900, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        fixture.Peers.Others = [other];
        List<RemoteExecutionRequest> requests = [];
        fixture.Dispatcher.Handler = (request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(NodeResult.Success());
        };
        WorkflowDefinition document = WithComments(Definition(), comments);
        CompilationResult compiled = fixture.Compiler.Compile(document);
        Assert.Empty(compiled.Issues);
        Assert.NotNull(compiled.Workflow);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(compiled.Workflow);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        RemoteExecutionRequest sent = Assert.Single(requests);
        Assert.Equal(other.InstanceId, sent.TargetInstanceId);
        Assert.Equal(2, report.Nodes.Single(node => string.Equals(node.NodeId, "run", StringComparison.Ordinal)).PcSelection!.CandidateHostIds.Count);
    }

    /// <summary>PC選択値を通常のJSON入力や設定へ流す接続を拒否します。</summary>
    /// <returns>保存前検証の確認完了。</returns>
    [Fact]
    public async Task SelectionPortsKeepTheirTypeAndAllowMultipleConnectionsAsync()
    {
        await using WorkflowFixture fixture = Fixture();
        WorkflowDefinition document = Definition();
        WireDefinition second = document.Wires[1];
        Assert.True(WorkflowConnectionTypes.CanReplaceConnection(document with { Wires = [document.Wires[0]] }, fixture.Registry.Descriptors, second));
        Assert.False(WorkflowConnectionTypes.CanConnect(document, fixture.Registry.Descriptors, second with { ToPort = "value" }));
        Assert.False(WorkflowConnectionTypes.CanConnect(document, fixture.Registry.Descriptors, second with { ToPort = "trigger" }));
        Assert.False(WorkflowConnectionTypes.CanConnect(document, fixture.Registry.Descriptors, second with { Path = "/0" }));
        Assert.False(WorkflowConnectionTypes.CanConnect(document, fixture.Registry.Descriptors, second with { When = "failure" }));
    }

    /// <summary>Commentを経由してもPC選択の専用入力境界を保ち、JSONへの取り違えを拒否します。</summary>
    /// <returns>編集時とコンパイル時に共通の不適合判定です。</returns>
    [Theory]
    [InlineData("value", null, "success")]
    [InlineData("trigger", null, "success")]
    [InlineData("$execution.pc", "/0", "success")]
    [InlineData("$execution.pc", null, "failure")]
    public async Task CommentsDoNotEraseSelectionBoundariesAsync(string port, string? path, string route)
    {
        await using WorkflowFixture fixture = Fixture();
        WorkflowDefinition document = WithComments(Definition(), 2);
        WireDefinition original = document.Wires[2];
        WireDefinition invalid = original with { ToPort = port, Path = path, When = route };
        document = document with { Wires = [.. document.Wires.Where(wire => wire != original), invalid] };
        Assert.False(WorkflowConnectionTypes.CanConnect(document, fixture.Registry.Descriptors, invalid));
        Assert.Contains(fixture.Compiler.Compile(document).Issues, issue => string.Equals(issue.Code, "wire.valueType", StringComparison.Ordinal));
    }

    /// <summary>PC条件と同じJSON構造の定数でも、Commentを通して実行先へ昇格させません。</summary>
    /// <returns>データ配列とPC選択型の境界です。</returns>
    [Fact]
    public async Task JsonThroughCommentDoesNotBecomePcSelectionAsync()
    {
        await using WorkflowFixture fixture = Fixture();
        WorkflowDefinition document = WithComments(Definition(), 1);
        NodeDefinition json = new("first", "value.constant", NodeSettings.Schema("""{"type":"array","text":"[{\"hostIds\":[\"host-a\"]}]"}"""));
        document = document with { Nodes = [json, .. document.Nodes.Skip(1)] };
        Assert.False(WorkflowConnectionTypes.CanConnect(document, fixture.Registry.Descriptors, document.Wires[1]));
        Assert.Contains(fixture.Compiler.Compile(document).Issues, issue => string.Equals(issue.Code, "wire.valueType", StringComparison.Ordinal));
    }

    /// <summary>従来の実行先との二重指定と、候補未指定の全PC実行を保存前に拒否します。</summary>
    /// <returns>設定不備の確認完了。</returns>
    [Fact]
    public async Task ConflictingTargetAndUnspecifiedAllAreRejectedAsync()
    {
        await using WorkflowFixture fixture = Fixture();
        WorkflowDefinition document = Definition();
        WorkflowDefinition duplicate = document with { Nodes = [.. document.Nodes.Take(2), document.Nodes[2] with { Target = new(EExecutionScope.One, ["a"]) }] };
        Assert.Contains(fixture.Compiler.Compile(duplicate).Issues, issue => string.Equals(issue.Code, "selection.conflictingTarget", StringComparison.Ordinal));
        WorkflowDefinition unspecified = document with { Nodes = [document.Nodes[2] with { PcExecution = EPcExecutionMode.All }], Wires = [] };
        Assert.Contains(fixture.Compiler.Compile(unspecified).Issues, issue => string.Equals(issue.Code, "selection.mode", StringComparison.Ordinal));
    }

    /// <summary>複数の候補入力を、一つのリソース選択条件へまとめます。</summary>
    /// <returns>選択条件の構築確認完了。</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task ResourceSelectorCombinesCandidateInputsWithoutExecutingMeasurementsAsync(int comments)
    {
        await using WorkflowFixture fixture = Fixture();
        WorkflowDefinition document = Definition();
        NodeDefinition lowest = new("lowest", PcSelectionPorts.LowestNode, NodeSettings.Schema("""{"resource":"cpu"}"""));
        document = document with
        {
            Nodes = [.. document.Nodes, lowest],
            Wires = [new("first", "value", "lowest", PcSelectionPorts.Candidates), new("second", "value", "lowest", PcSelectionPorts.Candidates), new("lowest", "value", "run", PcSelectionPorts.Execution)],
        };
        document = WithComments(document, comments);
        CompilationResult compiled = fixture.Compiler.Compile(document);
        Assert.Empty(compiled.Issues);
        PcSelection selection = Assert.Single(compiled.Workflow!.PcSelections!["run"]);
        Assert.Equal(EPcResource.Cpu, selection.Lowest!.Resource);
        Assert.Equal(2, selection.Within!.Count);
    }

    /// <summary>PC選択と通常の有限処理を同じ実ランタイムへ登録します。</summary>
    /// <returns>所有する待受を終了できる試験環境。</returns>
    private static WorkflowFixture Fixture() => new(new PcSelectionNode(), new LowestResourcePcNode(), new ConstantValueNode(), new CommentNode(),
        new WorkflowFixture.ActionNode((_, _) => ValueTask.FromResult(NodeResult.Success())));

    /// <summary>各PC選択配線へ同数のCommentを挟み、複数入力も維持します。</summary>
    /// <returns>中継ノードを含む同じPC選択です。</returns>
    private static WorkflowDefinition WithComments(WorkflowDefinition document, int count)
    {
        List<NodeDefinition> nodes = [.. document.Nodes];
        List<WireDefinition> wires = [];
        foreach (WireDefinition original in document.Wires)
        {
            WireDefinition wire = original;
            for (int index = 0; index < count; index++)
            {
                string id = original.FromNode + "-" + original.ToNode + "-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                nodes.Add(new(id, "value.comment", NodeSettings.Schema("""{"comment":"Selected PC"}""")));
                wires.Add(wire with { ToNode = id, ToPort = "value" });
                wire = wire with { FromNode = id, FromPort = "value" };
            }
            wires.Add(wire);
        }
        return document with { Nodes = nodes, Wires = wires };
    }

    /// <summary>別々に選んだPCを同じ処理へ接続します。</summary>
    /// <returns>通常の起動入力を増やさないPC選択付きのワークフロー。</returns>
    private static WorkflowDefinition Definition() => new("select", "PC selection", [
        new("first", PcSelectionPorts.SelectNode, NodeSettings.Schema("""{"mode":"selected","hostIds":["host-a"]}""")),
        new("second", PcSelectionPorts.SelectNode, NodeSettings.Schema("""{"mode":"selected","hostIds":["host-z"]}""")),
        new("run", "test.action", NodeSettings.Schema("{}")),
    ], [new("first", "value", "run", PcSelectionPorts.Execution), new("second", "value", "run", PcSelectionPorts.Execution)]);
}
