using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Inspection;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Management;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>転送元Hostの表示・互換設定・パス閲覧を確認します。</summary>
public sealed class FileTransferFieldTests
{
    /// <summary>新規ノードではPeer指定を隠し、旧保存値と接続は保持します。</summary>
    [Fact]
    public void SourceHostEditingPreservesLegacyDefinitionsUntilExplicitlyChanged()
    {
        JsonElement schema = JsonSerializer.Deserialize<JsonElement>("""{"properties":{"sourceHost":{"type":"string"},"sourcePeer":{"type":"string"}}}""");
        NodeDefinition node = new("transfer", "file.transfer", JsonSerializer.Deserialize<JsonElement>("{}"));
        NodeDescriptor descriptor = new(node.Type, node.Type, "Files", "", ENodeKind.Action, EExecutionLocation.ExplicitPeer, [], [], [], schema);
        WorkflowDefinition workflow = new("flow", "flow", [node], []);
        JsonElement fresh = EditorNodeDescriptor.Resolve(descriptor, node, workflow)!.SettingsSchema.GetProperty("properties");
        Assert.True(fresh.TryGetProperty("sourceHost", out _));
        Assert.False(fresh.TryGetProperty("sourcePeer", out _));
        Assert.False(fresh.TryGetProperty("clusterId", out _));
        NodeDefinition legacy = node with { Settings = JsonSerializer.SerializeToElement(new { sourcePeer = "old", path = "/destination" }) };
        Assert.True(EditorNodeDescriptor.Resolve(descriptor, legacy, workflow with { Nodes = [legacy] })!.SettingsSchema.GetProperty("properties").TryGetProperty("sourcePeer", out _));
        System.Text.Json.Nodes.JsonObject edited = ResourceSettingContext.EditSettings(legacy.Settings, "sourceHost", "pc");
        Assert.False(edited.ContainsKey("sourcePeer"));
        Assert.Equal("/destination", edited["path"]!.GetValue<string>());
        Assert.Equal("old", legacy.Settings.GetProperty("sourcePeer").GetString());
        Assert.True(ResourceSettingContext.EditSettings(legacy.Settings, "sourceHost", string.Empty).ContainsKey("sourcePeer"));
        WorkflowDefinition connected = workflow with { Wires = [new("source", "value", node.Id, SettingPorts.Name("sourcePeer"))] };
        Assert.True(EditorNodeDescriptor.Resolve(descriptor, node, connected)!.SettingsSchema.GetProperty("properties").TryGetProperty("sourcePeer", out _));
    }

    /// <summary>Hostは一台一候補となり、パス閲覧も同じ優先Peerへ向かいます。</summary>
    /// <returns>描画結果と閲覧先の確認です。</returns>
    [Fact]
    public async Task HostDropdownAndSourcePathUseTheWorkflowClusterAsync()
    {
        PeerInfo first = new("first", Guid.NewGuid(), "pc", "127.0.0.1", 1, EPeerStatus.Ready, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow);
        PeerInfo second = first with { PeerId = "preferred", InstanceId = Guid.NewGuid(), Priority = 10 };
        ServiceStatus status = new(first.PeerId, first.InstanceId, first.PeerId, EPeerStatus.Ready, [first, second], [], [], null, JsonSerializer.SerializeToElement(new { }));
        using ClientHttpHandler handler = new((request, _) =>
        {
            Assert.Equal("workflow-cluster", Assert.Single(request.Headers.GetValues("X-Contents-Cluster")));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(status, options: ContractJson.Options) });
        });
        using HttpClient http = new(handler) { BaseAddress = new Uri("http://localhost/") };
        ManagementApi api = new(http) { SelectedClusterId = "workflow-cluster", SelectedPeerId = first.PeerId };
        ConnectionState connection = new(api);
        await connection.RefreshAsync();
        SettingChoiceProvider choices = new(api, connection);
        NodeDefinition node = new("transfer", "file.transfer", JsonSerializer.SerializeToElement(new { sourceHost = "pc" }));
        Assert.Equal(second.PeerId, ResourceSettingContext.Peer(node, "sourcePath", new("flow", "flow", [node], []), choices));
        Assert.Null(choices.ResolveHost("missing"));
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(connection);
        services.AddSingleton(new UiText(new(new BrowserTestRuntime())));
        using ServiceProvider provider = services.BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = renderer.ConfigureAwait(true);
        string html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            HtmlRootComponent output = await renderer.RenderComponentAsync<PeerField>(ParameterView.FromDictionary(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [nameof(PeerField.Hosts)] = true, [nameof(PeerField.Value)] = "saved-missing",
            })).ConfigureAwait(true);
            return WebUtility.HtmlDecode(output.ToHtmlString());
        });
        Assert.Equal(1, html.Split("value=\"pc\"", StringSplitOptions.None).Length - 1);
        Assert.Contains("saved-missing — Not in current choices", html, StringComparison.Ordinal);
        Assert.DoesNotContain("preferred", html, StringComparison.Ordinal);
    }
}
