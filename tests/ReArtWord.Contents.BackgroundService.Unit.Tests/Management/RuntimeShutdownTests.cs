using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>Host終了が受付済みの長いノード待機で固まらないことを確認します。</summary>
public sealed class RuntimeShutdownTests
{
    /// <summary>依頼元の取消とは独立した実行にも、所有Host終了時は取消を伝えます。</summary>
    /// <returns>長時間待機の終了と非成功結果の確認です。</returns>
    [Fact(Timeout = 20000)]
    public async Task ShutdownCancelsAdmittedLongRunningNodeBeforeDrainingReceipts()
    {
        string root = Path.Combine(Path.GetTempPath(), "contents-shutdown-" + Guid.NewGuid().ToString("N"));
        using UdpClient socket = new(new IPEndPoint(IPAddress.Any, 0));
        int port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        socket.Close();
        Task<NodeResult>? execution = null;
        try
        {
            ManagementTestPeer peer = new(root, Guid.NewGuid().ToString("N"), "peer", port, true);
            await using (peer.ConfigureAwait(true))
            {
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(12));
                await peer.Runtime.Maintenance.LoadAsync(timeout.Token).ConfigureAwait(true);
                await peer.Cluster.StartAsync(timeout.Token).ConfigureAwait(true);
                while (peer.Cluster.GetPeers().Single().Status != EPeerStatus.Ready)
                {
                    await Task.Delay(20, timeout.Token).ConfigureAwait(true);
                }
                await peer.Runtime.Operational.InitializeAsync(new("UTC", peer.Runtime.Configuration.Files.ConflictPolicy,
                    peer.Runtime.Configuration.Files.InternalFailurePolicy, new Dictionary<string, IReadOnlyList<string>>()), true, timeout.Token).ConfigureAwait(true);
                NodeDefinition node = new("waiting", "control.delay", JsonSerializer.SerializeToElement(new { milliseconds = 60000 }));
                RemoteExecutionRequest request = new(Guid.NewGuid(), Guid.NewGuid(), "shutdown", "one", node,
                    new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(42) }, "peer", peer.Cluster.LocalInstanceId, DateTimeOffset.UtcNow.AddMinutes(1));
                execution = peer.Cluster.ExecuteAsync(request);
                while (!peer.Runtime.Executor.Running.Contains(request.Id))
                {
                    if (execution.IsCompleted)
                    {
                        Assert.Fail(JsonSerializer.Serialize(await execution.ConfigureAwait(true)));
                    }
                    await Task.Delay(10, timeout.Token).ConfigureAwait(true);
                }
            }
            Assert.NotEqual(EOutcome.Succeeded, (await execution.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(true)).Outcome);
        }
        finally
        {
            try
            {
                if (execution is not null)
                {
                    // Host終了による取消を検証するため、呼出元からは取り消さず結果を回収します。
                    await execution.ConfigureAwait(true);
                }
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
}
