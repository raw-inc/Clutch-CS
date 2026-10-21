using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.History;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>同じ待受ノードのPC別割り当てを、独立Coreと実通信で検証します。</summary>
public sealed class PerHostNetworkProcessTests
{
    /// <summary>有限ノードの遠隔配送でも実行PCのHostを使い、各PCから一回ずつ送信します。</summary>
    /// <returns>NodeExecutor経由の実行文脈と実UDP送信の確認です。</returns>
    [Fact]
    public async Task RemoteActionsSelectTheirExecutionHostAsync()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true, "host-a", priority: 10), second = new(cluster, "b", port, false, "host-b", priority: 1);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("peers").EnumerateArray()
            .Count(peer => string.Equals(peer.GetProperty("status").GetString(), "ready", StringComparison.Ordinal)) == 2).ConfigureAwait(true);
        using UdpClient receiver = new(new IPEndPoint(IPAddress.Loopback, 0));
        HostNetworkBinding[] bindings = [new("host-a", first.InterfaceId, "127.0.0.1"), new("host-b", second.InterfaceId, "127.0.0.1")];
        JsonElement settings = JsonSerializer.SerializeToElement(new { networkBindingMode = "perHost", networkBindings = bindings, remoteAddress = "127.0.0.1", remotePort = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port }, ContractJson.Options);
        WorkflowDefinition definition = new("host-send", "Host send", [new("send", "udp.send", settings, new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject("host payload") }, Target: new(EExecutionScope.All, ["a", "b"]))], []);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        WorkflowRunRequest request = new(Guid.NewGuid(), definition.Id, saved.Revision);
        WorkflowRunStatus status = await first.PostAsync<WorkflowRunStatus>("runs.start", request).ConfigureAwait(true);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        UdpReceiveResult a = await receiver.ReceiveAsync(deadline.Token).ConfigureAwait(true);
        UdpReceiveResult b = await receiver.ReceiveAsync(deadline.Token).ConfigureAwait(true);
        Assert.Equal("host payload", Encoding.UTF8.GetString(a.Buffer));
        Assert.Equal(a.Buffer, b.Buffer);
        Assert.NotEqual(a.RemoteEndPoint.Port, b.RemoteEndPoint.Port);
        await CorePeer.WaitAsync(async token =>
        {
            status = await first.GetAsync<WorkflowRunStatus>("runs.read", request.OperationId.ToString("D"), token).ConfigureAwait(true);
            return !status.Running;
        }, deadline.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, status.Outcome);
        Assert.Equal(2, Assert.Single(status.Report!.Nodes).Targets.Count);
    }

    /// <summary>各Hostで待受を開始し、一台の未割り当ては他Hostの受信を止めません。</summary>
    /// <returns>共有保存、Source文脈のHost、実HTTP／OSC受信の確認です。</returns>
    [Theory]
    [InlineData("http.listen", true)]
    [InlineData("http.listen", false)]
    [InlineData("osc.udp.receive", true)]
    [InlineData("osc.udp.receive", false)]
    public async Task SharedListenerUsesEachHostAssignmentAsync(string type, bool assignSecond)
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true, "host-a", priority: 10), second = new(cluster, "b", port, false, "host-b", priority: 1);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("peers").EnumerateArray()
            .Count(peer => string.Equals(peer.GetProperty("status").GetString(), "ready", StringComparison.Ordinal)) == 2).ConfigureAwait(true);
        List<HostNetworkBinding> bindings = [new("host-a", first.InterfaceId, "127.0.0.1")];
        if (assignSecond)
        {
            bindings.Add(new("host-b", second.InterfaceId, "127.0.0.1"));
        }
        JsonElement settings = JsonSerializer.SerializeToElement(new { networkBindingMode = "perHost", networkBindings = bindings, interfaceId = "unused-legacy-adapter", localAddress = "192.0.2.1", localPort = 0, count = 1 }, ContractJson.Options);
        WorkflowDefinition definition = new("host-bindings", "Host bindings", [new("listen", type, settings, Target: new(EExecutionScope.All, ["a", "b"]), StartOnLoad: true)], [], Enabled: true);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null)).ConfigureAwait(true);
        Assert.True(saved.Activation.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(saved, ContractJson.Options));
        string firstEndpoint = await WaitSourceAsync(first, true).ConfigureAwait(true);
        string secondEndpoint = await WaitSourceAsync(second, assignSecond).ConfigureAwait(true);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        await SendAsync(type, firstEndpoint, deadline.Token).ConfigureAwait(true);
        if (assignSecond)
        {
            Assert.NotEqual(firstEndpoint, secondEndpoint);
            await SendAsync(type, secondEndpoint, deadline.Token).ConfigureAwait(true);
        }
        int expected = assignSecond ? 2
                                    : 1;
        await CorePeer.WaitAsync(async token =>
        {
            ExecutionHistoryPage[] history = await Task.WhenAll(new[] { first, second }.Select(peer => peer.GetAsync<ExecutionHistoryPage>("executions.list", token))).ConfigureAwait(true);
            string workflow = "host-bindings", source = "listen";
            TargetNodeResult[] received = history.SelectMany(page => page.Entries).Select(entry => entry.Report).OfType<ExecutionReport>().Where(report => string.Equals(report.WorkflowId, workflow, StringComparison.Ordinal))
                .SelectMany(report => report.Nodes).Where(node => string.Equals(node.NodeId, source, StringComparison.Ordinal)).SelectMany(node => node.Targets)
                .Where(target => target.Result.Outcome == EOutcome.Succeeded && target.Result.Outputs.ContainsKey("value")).ToArray();
            return received.Select(item => item.PeerId).Distinct(StringComparer.Ordinal).Count() == expected;
        }, deadline.Token).ConfigureAwait(true);
    }

    /// <summary>一台のSourceの実待受または未割り当てによる開始拒否を確認します。</summary>
    /// <returns>実際の待受アドレスです。未割り当ては空文字です。</returns>
    private static async Task<string> WaitSourceAsync(CorePeer peer, bool assigned, CancellationToken cancellationToken = default)
    {
        string endpoint = string.Empty;
        await CorePeer.WaitAsync(async token =>
        {
            JsonElement status = await peer.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
            JsonElement[] sources = status.GetProperty("sources").EnumerateArray().ToArray();
            if (sources.Length != 1)
            {
                return false;
            }
            JsonElement state = sources[0].GetProperty("status");
            if (!assigned)
            {
                string reason = "network.binding.missing", reasonKey = "reasonCode";
                return state.TryGetProperty(reasonKey, out JsonElement code) && string.Equals(code.GetString(), reason, StringComparison.Ordinal);
            }
            string listening = "listening", stateKey = "state";
            if (!string.Equals(state.GetProperty(stateKey).GetString(), listening, StringComparison.Ordinal))
            {
                return false;
            }
            endpoint = state.GetProperty("message").GetString()!;
            return true;
        }, cancellationToken).ConfigureAwait(true);
        return endpoint;
    }

    /// <summary>待受の実アドレスへ一件だけ入力を送ります。</summary>
    /// <returns>HTTPまたはUDPでの送信完了です。</returns>
    private static async Task SendAsync(string type, string endpoint, CancellationToken cancellationToken = default)
    {
        string http = "http.listen";
        if (string.Equals(type, http, StringComparison.Ordinal))
        {
            using HttpClient client = new();
            using HttpResponseMessage response = await client.GetAsync(new Uri(endpoint), cancellationToken).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return;
        }
        using UdpClient sender = new();
        await sender.SendAsync(Encoding.ASCII.GetBytes("/cue\0\0\0\0,\0\0\0"), IPEndPoint.Parse(endpoint), cancellationToken).ConfigureAwait(true);
    }
}
