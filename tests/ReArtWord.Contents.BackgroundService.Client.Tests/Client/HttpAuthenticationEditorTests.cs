using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Inspection;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>認証なしの編集と、停止中NICの表示を保存内容の変更なしで確認します。</summary>
public sealed class HttpAuthenticationEditorTests
{
    /// <summary>認証なしでは認証欄を省略し、旧設定と接続済みの方式は保持します。</summary>
    [Theory]
    [InlineData("none", false, false)]
    [InlineData("none", true, true)]
    [InlineData("auto", false, true)]
    [InlineData("secret", false, true)]
    public void InactiveAuthenticationFieldsShareTheProjectedSchema(string mode, bool connected, bool visible)
    {
        JsonElement schema = JsonSerializer.Deserialize<JsonElement>("""
            {"type":"object","properties":{
              "authentication":{"type":"string","enum":["auto","none","secret"],"default":"auto"},
              "secretId":{"type":"string","x-visibleWhen":{"property":"authentication","values":["auto","secret"],"default":"auto"}},
              "secretHeader":{"type":"string","x-visibleWhen":{"property":"authentication","values":["auto","secret"],"default":"auto"}},
              "secretScheme":{"type":"string","x-visibleWhen":{"property":"authentication","values":["auto","secret"],"default":"auto"}}}}
            """);
        NodeDefinition node = new("request", "http.request", JsonSerializer.SerializeToElement(new { authentication = mode, secretId = "preserved", secretHeader = "X-Api-Key", secretScheme = "" }));
        WireDefinition[] wires = [];
        if (connected)
        {
            wires = [new("unknown", "value", node.Id, SettingPorts.Name("authentication"))];
        }
        WorkflowDefinition definition = new("flow", "Flow", [node], wires);
        string original = ContractJson.Hash(definition);
        NodeDescriptor descriptor = new(node.Type, "HTTP", "Communication", "", ENodeKind.Action, EExecutionLocation.ExplicitPeer, [], [new("value")], ["success"], schema);
        NodeDescriptor result = EditorNodeDescriptor.Editable(descriptor, node, definition)!;
        foreach (string field in new[] { "secretId", "secretHeader", "secretScheme" })
        {
            Assert.Equal(visible, result.SettingsSchema.GetProperty("properties").TryGetProperty(field, out _));
        }
        Assert.Equal(original, ContractJson.Hash(definition));
        Assert.Equal("preserved", node.Settings.GetProperty("secretId").GetString());
    }

    /// <summary>同じ名前のVPNでも稼働状態を識別でき、古いPeerの未取得を停止中と扱いません。</summary>
    [Fact]
    public void NetworkInventoryKeepsObservedStatusWithoutDroppingSavedChoices()
    {
        JsonElement response = JsonSerializer.Deserialize<JsonElement>("""
            [{"id":"active","name":"VPN","status":"Up","addresses":["10.0.0.1"]},
             {"id":"down","name":"VPN","status":"Down","addresses":["169.254.1.1"]},
             {"id":"legacy","name":"VPN","addresses":["192.0.2.1"]}]
            """);
        SettingNetworkChoices choices = SettingChoiceProvider.Parse("peer", response);
        UiText ui = new(new(new BrowserTestRuntime()));
        Assert.Equal(3, choices.Interfaces.Count);
        Assert.False(choices.Interfaces[0].Unavailable);
        Assert.True(choices.Interfaces[1].Unavailable);
        Assert.False(choices.Interfaces[2].Unavailable);
        Assert.Contains("Up", choices.Interfaces[0].DisplayName(ui), StringComparison.Ordinal);
        Assert.Contains("Down / unavailable", choices.Interfaces[1].DisplayName(ui), StringComparison.Ordinal);
        Assert.Contains("Status unverified", choices.Interfaces[2].DisplayName(ui), StringComparison.Ordinal);
        Assert.Equal("169.254.1.1", Assert.Single(choices.Interfaces[1].Addresses));
    }
}
