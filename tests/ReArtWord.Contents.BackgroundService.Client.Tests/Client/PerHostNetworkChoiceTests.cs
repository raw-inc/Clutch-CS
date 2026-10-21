using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.Management;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>複数PCのNIC候補を混ぜず、PC選択と未検出の保存値を扱います。</summary>
public sealed class PerHostNetworkChoiceTests
{
    /// <summary>同名のNICも各Hostへ別照会し、未検出Hostを選択中Peerへ代替しません。</summary>
    /// <returns>照会先、候補の分離、接続選択を維持する確認です。</returns>
    [Fact]
    public async Task HostInventoriesStaySeparateAndMissingHostDoesNotFallbackAsync()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        PeerInfo first = new("a", Guid.NewGuid(), "host-a", "127.0.0.1", 12345, EPeerStatus.Ready, 10, now, now);
        PeerInfo second = new("b", Guid.NewGuid(), "host-b", "127.0.0.1", 12345, EPeerStatus.Ready, 1, now, now);
        ServiceStatus status = new("a", first.InstanceId, "a", EPeerStatus.Ready, [first, second], [], [], null, JsonSerializer.SerializeToElement(new { }));
        List<string> requested = [];
        using ClientHttpHandler handler = new((request, _) =>
        {
            object response = status;
            string path = "/api/network";
            if (string.Equals(request.RequestUri!.AbsolutePath, path, StringComparison.Ordinal))
            {
                string peer = request.Headers.GetValues("X-Contents-Peer").Single();
                requested.Add(peer);
                string address = "192.0.2.20";
                if (string.Equals(peer, first.PeerId, StringComparison.Ordinal))
                {
                    address = "192.0.2.10";
                }
                response = new[] { new { id = "nic-" + peer, name = "Ethernet", addresses = new[] { address } } };
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response, options: ContractJson.Options) });
        });
        using HttpClient http = new(handler) { BaseAddress = new Uri("http://localhost/") };
        ManagementApi api = new(http);
        ConnectionState connection = new(api);
        await connection.SelectTargetAsync("cluster", "a").ConfigureAwait(true);
        EditorDocument model = new(new(new BrowserTestRuntime()));
        JsonElement settings = JsonSerializer.SerializeToElement(new { networkBindingMode = "perHost", networkBindings = new[] { new HostNetworkBinding("host-b", "saved", "192.0.2.20") } }, ContractJson.Options);
        NodeDefinition node = new("listen", "http.listen", settings, Target: new(EExecutionScope.All, ["a", "b"]));
        await model.LoadAsync(new(new("flow", "Network", [node], []), "revision", now, EActivationPolicy.Auto)).ConfigureAwait(true);
        SettingChoiceProvider choices = new(api, connection, model);
        Assert.Equal(["host-a", "host-b"], choices.NetworkHosts(node));
        SettingNetworkChoices a = await choices.GetForHostAsync("host-a").ConfigureAwait(true);
        SettingNetworkChoices b = await choices.GetForHostAsync("host-b").ConfigureAwait(true);
        Assert.Equal("nic-a", Assert.Single(a.Interfaces).Id);
        Assert.Equal("192.0.2.20", Assert.Single(Assert.Single(b.Interfaces).Addresses));
        Assert.Equal(["a", "b"], requested);
        status = status with { Peers = [first] };
        await connection.RefreshAsync().ConfigureAwait(true);
        Assert.NotNull((await choices.GetForHostAsync("host-b", true).ConfigureAwait(true)).Problem);
        Assert.Equal(["a", "b"], requested);
        Assert.True(JsonElement.DeepEquals(settings, model.Draft.Document.Nodes[0].Settings));
        Assert.Equal("a", api.SelectedPeerId);
    }
}
