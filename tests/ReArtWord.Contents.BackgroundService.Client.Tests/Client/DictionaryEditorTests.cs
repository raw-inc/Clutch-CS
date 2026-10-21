using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>辞書のキー・値の型を表示し、反復への配線を受け付けます。</summary>
public sealed class DictionaryEditorTests
{
    /// <summary>日本語と英語の表示型に、構築した辞書の値型を補います。</summary>
    /// <returns>表示ラベルと配線判定です。</returns>
    [Fact]
    public async Task ShowsDictionaryTypesAndAllowsBothIterationNodesAsync()
    {
        JsonElement empty = JsonSerializer.SerializeToElement(new { });
        NodeDescriptor dictionary = new("value.dictionary", "Build dictionary", "Values", "", ENodeKind.Action, EExecutionLocation.Independent,
            [new("items", EPortValueKind.Json), new("item", EPortValueKind.Json)], [new("value", EPortValueKind.Json)], ["success"], JsonSerializer.Deserialize<JsonElement>(DictionaryValues.SettingsSchema), CanEvaluateStatically: true);
        NodeDefinition value = new("dictionary", dictionary.Type, empty, new Dictionary<string, NodeValue> { ["items"] = NodeValue.FromObject(new[] { new { A = 1 }, new { A = 2 } }) });
        value = value with { Settings = JsonSerializer.SerializeToElement(new { duplicate = "replace" }) };
        UiText text = new(new(new BrowserTestRuntime()));
        await text.InitializeAsync().ConfigureAwait(true);
        foreach (string kind in new[] { "control.forEach", "control.parallelForEach" })
        {
            NodeDescriptor loop = new(kind, kind, "Control", "", ENodeKind.Action, EExecutionLocation.ExplicitPeer, [new("value", EPortValueKind.Json)], [new("item", EPortValueKind.Json)], ["success"], empty);
            WorkflowDefinition workflow = new("dictionary", "Dictionary", [value, new("loop", kind, empty)], []);
            NodeDescriptor[] catalog = [dictionary, loop];
            GraphPortPresentation port = new GraphTypeInference(workflow, catalog).Port(value.Id, "value", true);
            Assert.Equal("string", port.DataType!.KeyType!.Type);
            Assert.Equal("integer", port.DataType.ValueType!.Type);
            Assert.Contains("Dictionary", port.DisplayLabel(text), StringComparison.Ordinal);
            Assert.True(GraphConnectionPolicy.CanConnect(workflow, catalog, new(value.Id, "value", "loop", "value")));
        }
        await text.ChangeAsync("ja").ConfigureAwait(true);
        Assert.Equal("Dictionary（辞書）", text.NodeName(dictionary));
        Assert.Equal("辞書〈文字列, 整数〉", new GraphPortPresentation("value", "json", "json", "", new("object", KeyType: new("string"), ValueType: new("integer"))).DisplayLabel(text));
    }
}
