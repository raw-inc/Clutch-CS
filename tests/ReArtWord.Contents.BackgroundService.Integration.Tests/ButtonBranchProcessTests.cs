using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>複数Coreと実UDPで、ボタンごとの値・送信先・Signalが混線しないことを確認します。</summary>
public sealed class ButtonBranchProcessTests
{
    /// <summary>十二個の入口を順に押し、選択された宛先にだけ一件送信します。</summary>
    /// <returns>レシピと複数の値合流を経由した実通信結果です。</returns>
    [Fact]
    public async Task EachButtonSendsItsOwnCueThroughMergedRecipeInputsAsync()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true, "host-a", 1000), second = new(cluster, "b", port, false, "host-b", 900);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        using UdpClient zone0 = new(new IPEndPoint(IPAddress.Loopback, 0)), zone2 = new(new IPEndPoint(IPAddress.Loopback, 0)), zone3 = new(new IPEndPoint(IPAddress.Loopback, 0)), zone5 = new(new IPEndPoint(IPAddress.Loopback, 0));
        Dictionary<int, UdpClient> receivers = new() { [0] = zone0, [2] = zone2, [3] = zone3, [5] = zone5 };
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90));
        await first.StartAsync(timeout.Token).ConfigureAwait(true);
        await second.StartAsync(timeout.Token).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("peers").EnumerateArray().Count(peer => peer.GetProperty("status").GetString() is "ready") == 2
            && (await first.GetAsync<ClusterOperationalState>("configuration.read", token).ConfigureAwait(true)).Effective is not null, timeout.Token).ConfigureAwait(true);
        WorkflowDefinition recipe = ButtonBranchWorkflow.Recipe(second.InterfaceId, receivers.ToDictionary(pair => pair.Key, pair => ((IPEndPoint)pair.Value.Client.LocalEndPoint!).Port));
        Assert.Equal(EOutcome.Succeeded, (await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), recipe, null), timeout.Token).ConfigureAwait(true)).Activation.Outcome);
        WorkflowDefinition workflow = ButtonBranchWorkflow.Definition();
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), workflow, null), timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        foreach (string cue in ButtonBranchWorkflow.Cues)
        {
            foreach (int zone in ButtonBranchWorkflow.Zones)
            {
                string button = ButtonBranchWorkflow.Button(zone, cue);
                WorkflowRunRequest request = new(Guid.NewGuid(), workflow.Id, saved.Revision, button);
                _ = await first.PostAsync<WorkflowRunStatus>("runs.start", request, timeout.Token).ConfigureAwait(true);
                WorkflowRunStatus? completed = null;
                await CorePeer.WaitAsync(async token =>
                {
                    completed = await first.GetAsync<WorkflowRunStatus>("runs.read", request.OperationId.ToString("D"), token).ConfigureAwait(true);
                    return !completed.Running && completed.Report is not null;
                }, timeout.Token).ConfigureAwait(true);
                Assert.Equal(EOutcome.Succeeded, completed!.Outcome);
                string destination = "send-zone" + zone.ToString(CultureInfo.InvariantCulture);
                NodeExecutionReport sent = Assert.Single(completed.Report!.Nodes, node => node.NodeId.EndsWith("/" + destination, StringComparison.Ordinal) && node.Outcome == EOutcome.Succeeded);
                Assert.Equal("b", Assert.Single(sent.Targets).PeerId);
                Assert.Equal(1, completed.Report.Nodes.Count(node => node.NodeId.Contains("/send-zone", StringComparison.Ordinal) && node.Outcome == EOutcome.Succeeded));
                Assert.Equal(1, completed.Report.Nodes.Count(node => node.NodeId.StartsWith("button-", StringComparison.Ordinal) && node.Outcome == EOutcome.Succeeded));
                UdpReceiveResult packet = await receivers[zone].ReceiveAsync(timeout.Token).ConfigureAwait(true);
                string address = "/zone/" + zone.ToString(CultureInfo.InvariantCulture) + "/cue/" + cue;
                byte[] expected = [.. OscString(address), .. OscString(",ss"), .. OscString("slot"), .. OscString(cue)];
                Assert.Equal(expected, packet.Buffer);
                Assert.All(receivers.Values, receiver => Assert.Equal(0, receiver.Available));
            }
        }
    }

    /// <summary>実装側のcodecを使わず、送信されたOSCの文字列形式を照合します。</summary>
    /// <returns>終端と4バイト境界のpaddingを持つ文字列です。</returns>
    private static byte[] OscString(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value + '\0');
        Array.Resize(ref bytes, (bytes.Length + 3) / 4 * 4);
        return bytes;
    }
}
