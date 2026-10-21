using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Inspection;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>表示する型と接続の許可、リソースの所有先が同じ意味になることを確認します。</summary>
public sealed class NodeContractConsistencyTests
{
    /// <summary>型入力には型定義と表示し、通常オブジェクトを既知の型として通しません。</summary>
    [Fact]
    public void TypeInputsShowTheirContractAndRejectOrdinaryObjects()
    {
        NodeDefinition invalid = Node("data", "value.constant", """{"type":"object","text":"{\"name\":\"not a type\"}"}""");
        NodeDefinition type = Node("type", "value.type", """{"type":"string"}""");
        NodeDefinition value = Node("value", "value.constant", """{"text":"hello"}""");
        NodeDescriptor[] catalog = [Descriptor("value.constant", [new("type", EPortValueKind.Json)]), Descriptor("value.type", [new("elementType"), new("properties")])];
        WireDefinition bad = new("data", "value", "value", "type");
        WorkflowDefinition workflow = new("types", "Types", [invalid, type, value], [bad]);
        Assert.Equal("type-definition", new GraphTypeInference(workflow, catalog).Port("value", "type", false).Key);
        Assert.Equal("type-definition", GraphPresentation.Port(catalog[0], "type", false).Key);
        Assert.False(WorkflowConnectionTypes.CanConnect(workflow, catalog, bad));
        Assert.True(WorkflowConnectionTypes.CanReplaceConnection(workflow, catalog, bad with { FromNode = "type" }));
        Assert.Empty(EditorNodeDescriptor.Resolve(catalog[1], type, workflow)!.Inputs);
        Assert.Equal("elementType", Assert.Single(EditorNodeDescriptor.Resolve(catalog[1], type with { Settings = JsonSerializer.Deserialize<JsonElement>("""{"type":"array"}""") }, workflow)!.Inputs).Name);
    }

    /// <summary>接続後のOSC形状を表示・接続検証に反映し、古い保存値は保持します。</summary>
    [Fact]
    public void ConnectedOscTypesUpdateTheVisiblePortsAndRejectIncompatibleValues()
    {
        NodeDefinition type = Node("type", "value.type", """{"type":"integer"}""");
        NodeDefinition send = Node("send", "osc.udp.send", """{"argumentTypes":["s","s"],"address":"/value"}""");
        NodeDefinition text = Node("text", "value.constant", """{"type":"string","text":"hello"}""");
        NodeDescriptor[] catalog = [Descriptor("value.type", []), Descriptor("value.constant", []),
            Descriptor("osc.udp.send", [new("value", Required: true), new("address")]) with { SettingsSchema = JsonSerializer.Deserialize<JsonElement>("""{"properties":{"argumentTypes":{"type":["array","object"]}}}""") }];
        WireDefinition types = new("type", "value", "send", SettingPorts.Name("argumentTypes"));
        WorkflowDefinition workflow = new("osc", "OSC", [type, send, text], [types]);
        Assert.True(WorkflowConnectionTypes.CanConnect(workflow, catalog, types));
        NodeDescriptor shown = EditorNodeDescriptor.Resolve(catalog[2], send, workflow)!;
        Assert.Contains(shown.Inputs, port => string.Equals(port.Name, "argument1", StringComparison.Ordinal));
        Assert.DoesNotContain(shown.Inputs, port => string.Equals(port.Name, "argument2", StringComparison.Ordinal));
        Assert.Equal("integer", new GraphTypeInference(workflow, catalog).Port("send", "argument1", false).Key);
        Assert.False(WorkflowConnectionTypes.CanConnect(workflow, catalog, new("text", "value", "send", "argument1")));
        Assert.Equal(2, send.Settings.GetProperty("argumentTypes").GetArrayLength());
    }

    /// <summary>要素型を明示した配列からもOSCのポートを構築できます。</summary>
    [Fact]
    public void ExplicitlyTypedTagArrayCanDefineOscPorts()
    {
        NodeDefinition type = Node("type", "value.type", """{"type":"string"}""");
        NodeDefinition tag = Node("tag", "value.constant", """{"type":"string","text":"i"}""");
        NodeDefinition array = Node("array", "value.array", "{}");
        NodeDefinition osc = Node("osc", "osc.udp.receive", "{}");
        WorkflowDefinition workflow = new("typed-tags", "Typed tags", [type, tag, array, osc],
            [new("type", "value", "array", "elementType"), new("tag", "value", "array", "item"), new("array", "value", "osc", "$settings.argumentTypes")]);
        NodeDefinition resolved = new WorkflowSettingValues(workflow).Resolve(osc);
        Assert.Equal(["i"], OscPorts.Types(resolved.Settings));
    }

    /// <summary>転送元の接続値と、宛先の実行ピアを別々に解決します。</summary>
    [Fact]
    public void FileBrowsingUsesSourcePeerForSourcePathsAndExecutionPeerForDestinations()
    {
        NodeDefinition selected = Node("peer", "value.peer", """{"peerId":"source"}""");
        NodeDefinition source = Node("path", "value.file", """{"path":"/data/file"}""");
        NodeDefinition transfer = Node("transfer", "file.transfer", """{"sourcePeer":"stale"}""") with { Target = new(EExecutionScope.One, ["destination"]) };
        WorkflowDefinition workflow = new("file", "File", [selected, source, transfer], [new("peer", "value", "transfer", "$settings.sourcePeer"), new("path", "value", "transfer", "$settings.sourcePath")]);
        using HttpClient http = new();
        SettingChoiceProvider choices = new(new ManagementApi(http));
        Assert.Equal("source", ResourceSettingContext.Peer(transfer, "sourcePath", workflow, choices));
        Assert.Equal("destination", ResourceSettingContext.Peer(transfer, "path", workflow, choices));
        Assert.Equal("source", ResourceSettingContext.Peer(source, "path", workflow, choices));
        Assert.False(ResourceSettingContext.IsPath("http.listen", "path"));
    }

    /// <summary>JSON設定を型を保ったまま用意します。</summary>
    /// <returns>保存用ノードです。</returns>
    private static NodeDefinition Node(string id, string type, string settings) => new(id, type, JsonSerializer.Deserialize<JsonElement>(settings));

    /// <summary>外部依存なしのカタログ宣言です。</summary>
    /// <returns>純粋値として接続可能な宣言です。</returns>
    private static NodeDescriptor Descriptor(string type, IReadOnlyList<PortDescriptor> inputs) => new(type, type, "Values", "", ENodeKind.Action, EExecutionLocation.ExplicitPeer,
        inputs, [new("value", EPortValueKind.Json)], ["success"], JsonSerializer.Deserialize<JsonElement>("{}"), CanEvaluateStatically: true);
}
