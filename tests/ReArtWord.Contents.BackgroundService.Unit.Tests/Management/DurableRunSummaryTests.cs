using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>結果詳細を運べなくても、確認済み成否を共有して再起動後に読めます。</summary>
public sealed class DurableRunSummaryTests
{
    /// <summary>全体4MiB超の実行結果を小さい確定結果として保存し、再実行しません。</summary>
    /// <returns>実UDP共有と再作成後の読取確認です。</returns>
    [Fact]
    public async Task LargeRunRemainsConfirmedOnOtherPeerAndAfterRestart()
    {
        string root = Path.Combine(Path.GetTempPath(), "contents-run-summary-" + Guid.NewGuid().ToString("N"));
        using UdpClient socket = new(new IPEndPoint(IPAddress.Any, 0));
        int port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        socket.Close();
        string clusterId = Guid.NewGuid().ToString("N");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        try
        {
            ManagementTestPeer first = new(Path.Combine(root, "first"), clusterId, "first", port, true);
            await using ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
            await first.Cluster.StartAsync(timeout.Token).ConfigureAwait(true);
            await first.Runtime.Operational.InitializeAsync(new("UTC", first.Runtime.Configuration.Files.ConflictPolicy,
                first.Runtime.Configuration.Files.InternalFailurePolicy, new Dictionary<string, IReadOnlyList<string>>()), true, timeout.Token).ConfigureAwait(true);
            ManagementTestPeer second = new(Path.Combine(root, "second"), clusterId, "second", port, false);
            await using ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
            await second.Cluster.StartAsync(timeout.Token).ConfigureAwait(true);
            while (first.Cluster.GetPeers().Count(static peer => peer.Status == EPeerStatus.Ready) != 2)
            {
                await Task.Delay(20, timeout.Token).ConfigureAwait(true);
            }
            first.Runtime.Engine.OperationalProvider = null;
            NodeValue output = NodeValue.FromBytes(new byte[200000]);
            int executed = 0;
            first.Cluster.RemoteExecutionHandler = (_, _) =>
            {
                Interlocked.Increment(ref executed);
                return Task.FromResult(NodeResult.Success(output));
            };
            NodeDefinition[] nodes = Enumerable.Range(0, 24).Select(index => new NodeDefinition("node" + index, "data.transform",
                JsonSerializer.SerializeToElement(new { operation = "create" }), Target: new TargetDefinition(EExecutionScope.One, ["first"]))).ToArray();
            WorkflowDefinition definition = new("large", "large", nodes, []);
            CompiledWorkflow plan = first.Runtime.Compiler.Compile(definition).Workflow!;
            Assert.NotNull(plan);
            await first.Runtime.Sources.ApplyAsync(plan, timeout.Token).ConfigureAwait(true);
            WorkflowRunRequest request = new(Guid.NewGuid(), definition.Id);
            await first.Runs.StartAsync(request, timeout.Token).ConfigureAwait(true);
            WorkflowRunStatus? confirmed;
            do
            {
                await Task.Delay(20, timeout.Token).ConfigureAwait(true);
                confirmed = await first.Runs.ReadAsync(request.OperationId, timeout.Token).ConfigureAwait(true);
            }
            while (confirmed?.Running != false);
            Assert.Equal(EOutcome.Succeeded, confirmed.Outcome);
            Assert.Null(confirmed.Report);
            Assert.Equal(24, confirmed.Summary!.TotalTargets);
            WorkflowRunStatus? other = await second.Runs.ReadAsync(request.OperationId, timeout.Token).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, other!.Outcome);
            Assert.Null(other.Report);
            Assert.Equal(24, other.Summary!.TotalTargets);
            ManagementTestPeer restarted = new(Path.Combine(root, "second"), clusterId, "second", port, false);
            await using ConfiguredAsyncDisposable restartedLifetime = restarted.ConfigureAwait(true);
            WorkflowRunStatus? restored = await restarted.Runs.ReadAsync(request.OperationId, timeout.Token).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, restored!.Outcome);
            Assert.Equal(24, restored.Summary!.Targets.Count);
            Assert.True(restored.DetailsUnavailable);
            Assert.True(JsonSerializer.SerializeToUtf8Bytes(restored, ContractJson.Options).Length < 131072);
            _ = await first.Runs.StartAsync(request, timeout.Token).ConfigureAwait(true);
            Assert.Equal(24, executed);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}
