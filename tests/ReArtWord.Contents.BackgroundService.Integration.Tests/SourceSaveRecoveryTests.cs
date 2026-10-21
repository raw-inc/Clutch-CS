using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>自動入力ONの保存を、実OSC・UDP・HTTP待受と別Coreの設定共有で確認します。</summary>
public sealed class SourceSaveRecoveryTests
{
    /// <summary>後続編集では接続を維持し、ポート変更時は自動再開して新しい版で受信します。</summary>
    /// <returns>受信なしでも更新される稼働表示と、実パケット配送の確認です。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task SaveKeepsListenerVisibleAndRestartsChangedBindingAsync(bool http, bool osc)
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true, "host-a"), second = new(cluster, "b", port, false, "host-b");
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90));
        await first.StartAsync(timeout.Token).ConfigureAwait(true);
        await second.StartAsync(timeout.Token).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("peers").EnumerateArray()
            .Count(peer => peer.GetProperty("status").GetString() is "ready") == 2, timeout.Token).ConfigureAwait(true);
        int inputPort = CorePeer.AvailablePort(tcp: http);
        string type = "udp.receive";
        if (http)
        {
            type = "http.listen";
        }
        if (osc)
        {
            type = "osc.udp.receive";
        }
        NodeDefinition[] nodes = [new("pc", "execution.pc.select", JsonSerializer.Deserialize<JsonElement>("""{"mode":"selected","hostIds":["host-b"]}""")),
            new("receive", type, Settings(second.InterfaceId, inputPort), StartOnLoad: true),
            new("preview", "control.preview", JsonSerializer.Deserialize<JsonElement>("""{"mediaType":"text/plain"}"""), Target: new(EExecutionScope.One, SourcePeer: true))];
        WorkflowDefinition definition = new("saved-source", "Saved source", nodes,
            [new("pc", "value", "receive", "$execution.pc"), new("receive", "value", "preview", "value")], Enabled: true);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null), timeout.Token).ConfigureAwait(true);
        Assert.True(saved.Activation.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(saved));
        JsonElement original = await WaitListeningAsync(second, saved.Revision!, timeout.Token).ConfigureAwait(true);
        Guid originalId = original.GetProperty("id").GetGuid();
        await SendAndObserveAsync(second, inputPort, http, osc, saved.Revision!, timeout.Token).ConfigureAwait(true);
        for (int edit = 1; edit <= 3; edit++)
        {
            if (edit == 2)
            {
                nodes = nodes.Select(node => node with { LogicalName = "受信経路のメモ" }).ToArray();
            }
            else
            {
                nodes[2] = nodes[2] with { Position = new(edit * 100, 100) };
            }
            definition = definition with { Nodes = nodes.ToArray() };
            saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, saved.Revision), timeout.Token).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
            JsonElement current = await WaitListeningAsync(second, saved.Revision!, timeout.Token).ConfigureAwait(true);
            Assert.Equal(originalId, current.GetProperty("id").GetGuid());
            WorkflowNodeActivity activity = await second.GetAsync<WorkflowNodeActivity>("workflows.activity", definition.Id, timeout.Token).ConfigureAwait(true);
            Assert.Contains(activity.Nodes, node => node.NodeId is "receive" && string.Equals(node.Revision, saved.Revision, StringComparison.Ordinal));
            await SendAndObserveAsync(second, inputPort, http, osc, saved.Revision!, timeout.Token).ConfigureAwait(true);
        }
        int changedPort = CorePeer.AvailablePort(tcp: http);
        nodes[1] = nodes[1] with { Settings = Settings(second.InterfaceId, changedPort) };
        definition = definition with { Nodes = nodes.ToArray() };
        saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, saved.Revision), timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        JsonElement restarted = await WaitListeningAsync(second, saved.Revision!, timeout.Token).ConfigureAwait(true);
        Assert.NotEqual(originalId, restarted.GetProperty("id").GetGuid());
        await SendAndObserveAsync(second, changedPort, http, osc, saved.Revision!, timeout.Token).ConfigureAwait(true);
    }

    /// <summary>同じ明示NICを使う待受設定です。</summary>
    /// <returns>ポート以外を変えない設定。</returns>
    private static JsonElement Settings(string nic, int port) => JsonSerializer.SerializeToElement(new { interfaceId = nic, localAddress = "127.0.0.1", localPort = port });

    /// <summary>新しいパケットで状態を更新させず、反映だけで新版の待受を確認します。</summary>
    /// <returns>実行中の待受です。</returns>
    private static async Task<JsonElement> WaitListeningAsync(CorePeer peer, string revision, CancellationToken cancellationToken = default)
    {
        JsonElement source = default;
        await CorePeer.WaitAsync(async token =>
        {
            source = (await peer.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("sources").EnumerateArray()
                .FirstOrDefault(item => item.GetProperty("nodeId").GetString() is "receive" && item.GetProperty("status").GetProperty("state").GetString() is "listening"
                    && string.Equals(item.GetProperty("revision").GetString(), revision, StringComparison.Ordinal));
            return source.ValueKind == JsonValueKind.Object;
        }, cancellationToken).ConfigureAwait(true);
        return source;
    }

    /// <summary>実通信を一回だけ送り、その版で後続が処理されたことを確認します。</summary>
    /// <returns>Previewまで届いた入力の確認です。</returns>
    private static async Task SendAndObserveAsync(CorePeer peer, int port, bool http, bool osc, string revision, CancellationToken cancellationToken = default)
    {
        if (http)
        {
            using HttpClient client = new();
            using HttpResponseMessage response = await client.GetAsync(new Uri("http://127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/"), cancellationToken).ConfigureAwait(true);
            Assert.True(response.IsSuccessStatusCode);
        }
        else
        {
            byte[] data = Encoding.UTF8.GetBytes("payload");
            if (osc)
            {
                data = Encoding.UTF8.GetBytes("/probe\0\0,s\0\0payload\0");
            }
            using UdpClient client = new(new IPEndPoint(IPAddress.Loopback, 0));
            await client.SendAsync(data, new IPEndPoint(IPAddress.Loopback, port), cancellationToken).ConfigureAwait(true);
        }
        await CorePeer.WaitAsync(async token => (await peer.GetAsync<NodePreviewSnapshot[]>("previews.list", new NodePreviewQuery("saved-source", ["preview"]), token).ConfigureAwait(true))
            .Any(snapshot => string.Equals(snapshot.Revision, revision, StringComparison.Ordinal) && snapshot.Problem is null), cancellationToken).ConfigureAwait(true);
    }
}
