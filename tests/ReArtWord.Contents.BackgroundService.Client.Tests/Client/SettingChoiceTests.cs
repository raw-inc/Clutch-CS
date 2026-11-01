using System.Net;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Client.Management;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>選択候補の欠落と設定接続を、保存値の損失なしで扱います。</summary>
public sealed class SettingChoiceTests
{
    /// <summary>PC選択の接続先とCoordinatorからNICを取得し、画面の選択Peerを実行先と決め付けません。</summary>
    /// <returns>固定候補・負荷依存・未指定のネットワーク候補の確認。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InventoryFollowsExecutionSelectionAndTheInterfaceConsumerAsync(bool comments)
    {
        EditorTestClock clock = new();
        Guid selectedInstance = Guid.NewGuid();
        ServiceStatus status = new("selected", selectedInstance, "leader", EPeerStatus.Ready, [
            new("selected", selectedInstance, "host-selected", "127.0.0.1", 1, EPeerStatus.Ready, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new("leader", Guid.NewGuid(), "host-leader", "127.0.0.1", 2, EPeerStatus.Ready, 900, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new("target", Guid.NewGuid(), "host-target", "127.0.0.1", 3, EPeerStatus.Ready, 100, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
        ], [], [], null, JsonSerializer.SerializeToElement(new { }));
        List<string> inventoryTargets = [];
        string[] loopback = ["127.0.0.1"];
        string[] selectedHosts = ["host-target"];
        using ClientHttpHandler handler = new((request, _) =>
        {
            object response = status;
            string networkPath = "/api/network";
            if (string.Equals(request.RequestUri!.AbsolutePath, networkPath, StringComparison.Ordinal))
            {
                string peer = request.Headers.GetValues("X-Contents-Peer").Single();
                inventoryTargets.Add(peer);
                response = new[] { new { id = "nic-" + peer, name = peer, addresses = loopback } };
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(response, ContractJson.Options), Encoding.UTF8, "application/json") });
        });
        using HttpClient http = new(handler) { BaseAddress = new Uri("http://localhost/") };
        ManagementApi api = new(http, new(clock));
        ConnectionState connection = new(api);
        await connection.SelectTargetAsync("cluster", "selected").ConfigureAwait(true);
        EditorDocument model = new(new(new BrowserTestRuntime()));
        WorkflowDefinition workflow = new("flow", "PC selection", [
            new("pc", PcSelectionPorts.SelectNode, JsonSerializer.SerializeToElement(new { mode = "selected", hostIds = selectedHosts })),
            new("interface", "value.interface", JsonSerializer.SerializeToElement(new { interfaceId = "saved" })),
            new("listen", "http.listen", JsonSerializer.SerializeToElement(new { })),
        ], [new("pc", "value", "listen", PcSelectionPorts.Execution), new("interface", "value", "listen", SettingPorts.Name("interfaceId"))]);
        if (comments)
        {
            workflow = workflow with
            {
                Nodes = [.. workflow.Nodes, new("comment", "value.comment", JsonSerializer.SerializeToElement(new { })), new("comment2", "value.comment", JsonSerializer.SerializeToElement(new { }))],
                Wires = [new("pc", "value", "comment", "value"), new("comment", "value", "comment2", "value"), new("comment2", "value", "listen", PcSelectionPorts.Execution), workflow.Wires[1]],
            };
        }
        await model.LoadAsync(new(workflow, "revision", DateTimeOffset.UtcNow, EActivationPolicy.Auto)).ConfigureAwait(true);
        SettingChoiceProvider provider = new(api, connection, model);
        Assert.Equal("target", (await provider.GetAsync(workflow.Nodes[2]).ConfigureAwait(true)).PeerId);
        Assert.Equal("target", (await provider.GetAsync(workflow.Nodes[1]).ConfigureAwait(true)).PeerId);
        Assert.Equal(["target"], inventoryTargets);
        model.ChangeNode("pc", node => node with { Type = PcSelectionPorts.LowestNode });
        Assert.Null((await provider.GetAsync(workflow.Nodes[2]).ConfigureAwait(true)).PeerId);
        Assert.Equal(["target"], inventoryTargets);
        model.Change(document => document with { Wires = [] });
        Assert.Equal("leader", (await provider.GetAsync(workflow.Nodes[2]).ConfigureAwait(true)).PeerId);
        Assert.Equal(["target", "leader"], inventoryTargets);
        Assert.Equal("selected", api.SelectedPeerId);
    }

    /// <summary>未知の保存値は勝手な先頭候補にならず、型も維持します。</summary>
    [Fact]
    public void MissingEnumIsNotReplacedOrThrown()
    {
        JsonElement options = JsonSerializer.Deserialize<JsonElement>("[\"GET\",\"POST\",9007199254740993]");
        Assert.True(SettingChoiceValues.Missing("FUTURE", options));
        Assert.False(SettingChoiceValues.TryEnum(options, "FUTURE", out _));
        Assert.True(SettingChoiceValues.TryEnum(options, "9007199254740993", out JsonElement integer));
        Assert.Equal(9007199254740993L, integer.GetInt64());
        Assert.False(SettingChoiceValues.Missing(string.Empty, options));
    }

    /// <summary>実行先を固定しClient自身のネットワークを取得しません。</summary>
    /// <returns>読み取り要求の検証です。</returns>
    [Fact]
    public async Task InventoryUsesExplicitPeerAndClusterAsync()
    {
        using InventoryHandler handler = new();
        using HttpClient http = new(handler) { BaseAddress = new Uri("http://localhost/") };
        ManagementApi api = new(http) { SelectedClusterId = "cluster", SelectedPeerId = "selected" };
        SettingChoiceProvider provider = new(api);
        NodeDefinition node = new("n", "value.interface", JsonSerializer.Deserialize<JsonElement>("{}"), Target: new(EExecutionScope.One, Peers: ["target"]));
        SettingNetworkChoices choices = await provider.GetAsync(node).ConfigureAwait(true);
        Assert.Equal("target", choices.PeerId);
        Assert.Equal("eth0", Assert.Single(choices.Interfaces).Id);
        Assert.Equal("192.0.2.10", Assert.Single(choices.Interfaces[0].Addresses));
        Assert.Null(choices.Problem);
        Assert.Null(SettingChoiceProvider.TargetPeer(node with { Target = new(EExecutionScope.All, Peers: ["a", "b"]) }, "selected"));
        Assert.Null(SettingChoiceProvider.TargetPeer(node with { Target = new(EExecutionScope.One, Groups: ["group"]) }, "selected"));
        Assert.Equal("selected", SettingChoiceProvider.TargetPeer(node with { Target = null }, "selected"));
    }

    /// <summary>設定接続は元の設定を残し、折畳み中も端点を保持します。</summary>
    /// <returns>Undoと形状の検証です。</returns>
    [Fact]
    public async Task SettingConnectionRetainsSavedValueAndUndoAsync()
    {
        EditorDocument model = new(new(new BrowserTestRuntime()));
        JsonElement settings = JsonSerializer.Deserialize<JsonElement>("{\"interfaceId\":\"saved-id\"}");
        WorkflowDefinition document = new("flow", "flow", [new("source", "value.interface", settings), new("target", "http.listen", settings)], []);
        await model.LoadAsync(new(document, "base", DateTimeOffset.UtcNow, EActivationPolicy.Auto)).ConfigureAwait(true);
        WireDefinition wire = new("source", "value", "target", SettingPorts.Name("interfaceId"));
        model.SetInput("target/settings/interfaceId", "unfinished raw");
        Assert.True(NodeInputEditing.Connect(model, wire));
        Assert.Null(model.ReadInput("target/settings/interfaceId"));
        Assert.Equal("saved-id", model.Draft.Document.Nodes[1].Settings.GetProperty("interfaceId").GetString());
        NodeDescriptor descriptor = new("http.listen", "HTTP", "Communication", "", ENodeKind.Source, EExecutionLocation.ExplicitPeer, [], [], [], JsonSerializer.Deserialize<JsonElement>("{\"properties\":{\"interfaceId\":{\"type\":\"string\"}}}"));
        Assert.Equal(wire.ToPort, Assert.Single(GraphSettingPorts.Collapsed(model.Draft.Document, "target", descriptor, false)));
        Assert.Empty(GraphSettingPorts.Collapsed(model.Draft.Document, "target", descriptor, true));
        Assert.True(GraphDeletion.Wire(model, 0));
        Assert.Equal("saved-id", model.Draft.Document.Nodes[1].Settings.GetProperty("interfaceId").GetString());
        model.Undo();
        Assert.Single(model.Draft.Document.Wires);
        model.Undo();
        Assert.Empty(model.Draft.Document.Wires);
        Assert.Equal("saved-id", model.Draft.Document.Nodes[1].Settings.GetProperty("interfaceId").GetString());
    }

    /// <summary>外部接続をせず管理要求の宛先を検証します。</summary>
    private sealed class InventoryHandler : HttpMessageHandler
    {
        /// <summary>固定対象の要求へNIC候補を返します。</summary>
        /// <returns>実行先の応答試験用の構成です。</returns>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/api/network", request.RequestUri!.AbsolutePath);
            Assert.Equal("target", Assert.Single(request.Headers.GetValues("X-Contents-Peer")));
            Assert.Equal("cluster", Assert.Single(request.Headers.GetValues("X-Contents-Cluster")));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[{\"id\":\"eth0\",\"name\":\"Ethernet\",\"addresses\":[\"192.0.2.10\"]}]", Encoding.UTF8, "application/json") });
        }
    }
}
