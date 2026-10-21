using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>別プロセスのPC選択、実配送、待受の担当共有を通常のUDP管理経路で確認します。</summary>
public sealed class PcSelectionClusterTests
{
    /// <summary>異なるHostの候補を集約し、最高優先度または各PCへ一回ずつ配送します。</summary>
    /// <returns>実Core間の保存・実行・結果取得の確認。</returns>
    [Theory]
    [InlineData(EPcExecutionMode.One, false, false)]
    [InlineData(EPcExecutionMode.All, false, false)]
    [InlineData(EPcExecutionMode.One, true, false)]
    [InlineData(EPcExecutionMode.All, true, false)]
    [InlineData(EPcExecutionMode.One, false, true)]
    [InlineData(EPcExecutionMode.All, true, true)]
    public async Task SelectedPcsExecuteWithTheRequestedCountAsync(EPcExecutionMode mode, bool comments, bool merge)
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true, "pc-a", 100);
        CorePeer second = new(cluster, "z", port, false, "pc-z", 900);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await ReadyAsync(first).ConfigureAwait(true);
        await ReadyAsync(second).ConfigureAwait(true);
        WorkflowDefinition definition = new("pc-selection", "Choose execution PCs", [
            Pc("pc-a", "pc-a"), Pc("pc-z", "pc-z"),
            new("add", "math.add", JsonSerializer.SerializeToElement(new { }), new Dictionary<string, NodeValue>(StringComparer.Ordinal)
            {
                ["left"] = NodeValue.FromObject(2), ["right"] = NodeValue.FromObject(3),
            }, PcExecution: mode),
        ], [new("pc-a", "value", "add", PcSelectionPorts.Execution), new("pc-z", "value", "add", PcSelectionPorts.Execution)]);
        if (comments)
        {
            definition = definition with
            {
                Nodes = [.. definition.Nodes, new("comment-a", "value.comment", JsonSerializer.SerializeToElement(new { })), new("comment-z", "value.comment", JsonSerializer.SerializeToElement(new { }))],
                Wires = [new("pc-a", "value", "comment-a", "value"), new("pc-z", "value", "comment-z", "value"),
                    new("comment-a", "value", "add", PcSelectionPorts.Execution), new("comment-z", "value", "add", PcSelectionPorts.Execution)],
            };
        }
        if (merge)
        {
            WireDefinition[] candidates = definition.Wires.Where(wire => string.Equals(wire.ToPort, PcSelectionPorts.Execution, StringComparison.Ordinal)).ToArray();
            definition = definition with
            {
                Nodes = [.. definition.Nodes, new("merge", ValueMergePorts.NodeType, JsonSerializer.SerializeToElement(new { }))],
                Wires = [.. definition.Wires.Except(candidates), .. candidates.Select((wire, index) => wire with { ToNode = "merge", ToPort = ConcatPorts.Name(index + 1) }), new("merge", "value", "add", PcSelectionPorts.Execution)],
            };
        }
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        WorkflowRunStatus finished = await RunAsync(first, definition.Id).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, finished.Outcome);
        NodeExecutionReport calculation = finished.Report!.Nodes.Single(node => string.Equals(node.NodeId, "add", StringComparison.Ordinal));
        if (merge)
        {
            Assert.Equal("a", Assert.Single(calculation.Targets).PeerId);
        }
        else
        {
            if (mode == EPcExecutionMode.One)
            {
                Assert.Equal("z", Assert.Single(calculation.Targets).PeerId);
            }
            else
            {
                Assert.Equal(["a", "z"], calculation.Targets.Select(target => target.PeerId).Order(StringComparer.Ordinal));
            }
        }
        Assert.All(calculation.Targets, target => Assert.Equal(5, target.Result.Outputs["value"].Json.GetDouble()));
        int candidatesCount = 2;
        if (merge)
        {
            candidatesCount = 1;
        }
        Assert.Equal(candidatesCount, calculation.PcSelection!.CandidateHostIds.Count);
    }

    /// <summary>PC選択で指定した待受は共有した担当だけで開始し、元の実体の指定で停止できます。</summary>
    /// <returns>待受担当の共有と実停止の確認。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceStartsOnlyOnItsSelectedPcAsync(bool comment)
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true, "pc-a", 900);
        CorePeer second = new(cluster, "b", port, false, "pc-b", 100);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await ReadyAsync(first).ConfigureAwait(true);
        await ReadyAsync(second).ConfigureAwait(true);
        WorkflowDefinition definition = new("selected-source", "Selected listener", [
            Pc("pc", "pc-b"),
            new("receive", "udp.receive", JsonSerializer.SerializeToElement(new { interfaceId = second.InterfaceId, localAddress = "127.0.0.1", localPort = CorePeer.AvailablePort() }), StartOnLoad: true),
        ], [new("pc", "value", "receive", PcSelectionPorts.Execution)], Enabled: true);
        if (comment)
        {
            definition = definition with
            {
                Nodes = [.. definition.Nodes, new("comment", "value.comment", JsonSerializer.SerializeToElement(new { }))],
                Wires = [new("pc", "value", "comment", "value"), new("comment", "value", "receive", PcSelectionPorts.Execution)],
            };
        }
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        JsonElement status = default;
        await CorePeer.WaitAsync(async token =>
        {
            status = await second.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
            return status.GetProperty("sources").EnumerateArray().Any(source => string.Equals(source.GetProperty("status").GetProperty("state").GetString(), "listening", StringComparison.Ordinal));
        }).ConfigureAwait(true);
        JsonElement local = await first.GetAsync<JsonElement>("status").ConfigureAwait(true);
        Assert.Empty(local.GetProperty("sources").EnumerateArray());
        JsonElement source = Assert.Single(status.GetProperty("sources").EnumerateArray());
        NodeResult stopped = await second.PostAsync<NodeResult>("sources.stop", new SourceStopRequest(definition.Id, "receive", SourceId: source.GetProperty("id").GetGuid(),
            PeerInstanceId: status.GetProperty("instanceId").GetGuid())).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, stopped.Outcome);
    }

    /// <summary>通常のPC選択ノードを一台の明示指定として作ります。</summary>
    /// <returns>実行PC入力へ接続するノード。</returns>
    private static NodeDefinition Pc(string id, string host) => new(id, PcSelectionPorts.SelectNode, JsonSerializer.SerializeToElement(new { mode = "selected", hostIds = new[] { host } }));

    /// <summary>両Peerの通常参加と、共通設定の初期化を待ちます。</summary>
    /// <returns>保存操作を開始できる状態の確認。</returns>
    private static Task ReadyAsync(CorePeer peer) => CorePeer.WaitAsync(async token =>
    {
        JsonElement status = await peer.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
        return string.Equals(status.GetProperty("status").GetString(), "ready", StringComparison.Ordinal)
            && status.GetProperty("peers").EnumerateArray().Count(item => string.Equals(item.GetProperty("status").GetString(), "ready", StringComparison.Ordinal)) == 2
            && (await peer.GetAsync<ClusterOperationalState>("configuration.read", token).ConfigureAwait(true)).Effective is not null;
    });

    /// <summary>一度受け付けた操作IDを照会し、実行を繰り返しません。</summary>
    /// <returns>同じ操作の最終結果。</returns>
    private static async Task<WorkflowRunStatus> RunAsync(CorePeer peer, string workflow)
    {
        WorkflowRunRequest request = new(Guid.NewGuid(), workflow);
        WorkflowRunStatus status = await peer.PostAsync<WorkflowRunStatus>("runs.start", request).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token =>
        {
            status = await peer.GetAsync<WorkflowRunStatus>("runs.read", request.OperationId.ToString("D"), token).ConfigureAwait(true);
            return !status.Running && status.Outcome != EOutcome.Unknown;
        }).ConfigureAwait(true);
        return status;
    }
}
