using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>キー検索の表示型が、形式設定とUndoへ追従することを確認します。</summary>
public sealed class KeyValueLookupEditorTests
{
    /// <summary>同じkey/valueという名前でも、明示Object形式ではプロパティとして扱います。</summary>
    /// <returns>出力値の型表示・接続可否・Undoの確認です。</returns>
    [Fact]
    public async Task FormatChangesUpdateTheDisplayedTypeAndConnectionPolicyAsync()
    {
        JsonElement settings = JsonSerializer.SerializeToElement(new { });
        NodeDefinition lookup = new("get", KeyValueLookup.NodeType, settings, new Dictionary<string, NodeValue>
        { ["value"] = NodeValue.FromJson(JsonSerializer.Deserialize<JsonElement>("[{\"key\":\"A\",\"value\":1}]")), ["key"] = NodeValue.FromObject("key"), ["defaultValue"] = NodeValue.FromObject(false) });
        WorkflowDefinition document = new("flow", "Lookup", [lookup, new("text", "text.encode", settings)], []);
        NodeDescriptor[] catalog = [
            new(KeyValueLookup.NodeType, "Lookup", "Data", "", ENodeKind.Action, EExecutionLocation.Independent,
                [new("value", EPortValueKind.Json, true) { DataType = new("array", new("object")) }, new("key", EPortValueKind.Json, true) { DataType = new("string") }, new("defaultValue")],
                [new("value")], ["success"], JsonSerializer.Deserialize<JsonElement>(KeyValueLookup.SettingsSchema), CanEvaluateStatically: true),
            new("text.encode", "Encode", "Data", "", ENodeKind.Action, EExecutionLocation.Independent, [new("value", EPortValueKind.Json, true) { DataType = new("string") }], [new("value", EPortValueKind.Binary)], ["success"], settings),
        ];
        EditorDocument model = new(new(new BrowserTestRuntime()));
        await model.LoadAsync(new(document, "base", DateTimeOffset.UtcNow, EActivationPolicy.Auto)).ConfigureAwait(true);
        Assert.Equal("boolean", new GraphTypeInference(model.Draft.Document, catalog).Port("get", "value", true).InferredType);
        Assert.False(GraphConnectionPolicy.CanConnect(model.Draft.Document, catalog, new("get", "value", "text", "value")));
        model.ChangeNode("get", node => node with { Settings = JsonSerializer.SerializeToElement(new { format = "objects" }) });
        Assert.Equal("string", new GraphTypeInference(model.Draft.Document, catalog).Port("get", "value", true).InferredType);
        Assert.True(GraphConnectionPolicy.CanConnect(model.Draft.Document, catalog, new("get", "value", "text", "value")));
        model.Undo();
        Assert.Equal("boolean", new GraphTypeInference(model.Draft.Document, catalog).Port("get", "value", true).InferredType);
    }

    /// <summary>形式の違いと欠落時だけの既定値を設定欄に表示します。</summary>
    /// <returns>英語と日本語の語彙確認です。</returns>
    [Fact]
    public async Task FormatsAndDefaultPortHaveClearLabelsAsync()
    {
        UiText text = new(new(new BrowserTestRuntime()));
        await text.InitializeAsync().ConfigureAwait(true);
        Assert.Equal("Automatic (per entry)", text.SettingChoiceName(KeyValueLookup.NodeType, "format", "auto"));
        await text.ChangeAsync("ja").ConfigureAwait(true);
        Assert.Equal("Object配列（既存Key-Valueノード）", text.SettingChoiceName(KeyValueLookup.NodeType, "format", "objects"));
        Assert.Equal("key/valueペア配列", text.SettingChoiceName(KeyValueLookup.NodeType, "format", "pairs"));
        Assert.Equal("デフォルト値（Keyがない場合）", text.PortName(KeyValueLookup.NodeType, "defaultValue", false));
    }
}
