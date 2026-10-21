using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>Pathの接続可否、色・型表示、静的プレビューを実行と同じ規則にします。</summary>
public sealed class PathPresentationTests
{
    /// <summary>Commentを前後へ挟んでも、逐次接続・差替え後の型とプレビューを維持します。</summary>
    /// <returns>実編集モデルでの接続と型表示の確認です。</returns>
    [Theory]
    [InlineData("value.constant")]
    [InlineData("value.directory")]
    [InlineData("data.convert")]
    public async Task CommentsAndIncrementalConnectionsRetainPathType(string sourceType)
    {
        BrowserTestRuntime runtime = new();
        EditorDocument model = new(new(runtime));
        JsonElement sourceSettings = sourceType switch
        {
            "value.constant" => JsonSerializer.SerializeToElement(new { type = "path", text = "root" }),
            "value.directory" => JsonSerializer.SerializeToElement(new { path = "root" }),
            _ => JsonSerializer.SerializeToElement(new { targetType = "path" }),
        };
        NodeDefinition root = new("root", sourceType, sourceSettings);
        NodeDefinition leaf = new("leaf", "value.constant", JsonSerializer.SerializeToElement(new { type = "path", text = "file.wav" }));
        NodeDefinition comment = new("comment", "value.comment", JsonSerializer.SerializeToElement(new { comment = "Path" }),
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["displayName"] = NodeValue.FromObject("Named path") });
        NodeDefinition concat = new("concat", "data.concat", JsonSerializer.SerializeToElement(new { }));
        NodeDefinition after = comment with { Id = "after" };
        WorkflowDefinition document = new("incremental-path", "Path", [root, leaf, comment, concat, after], []);
        NodeDescriptor[] catalog = [Descriptor("value.constant", [], true), Descriptor("value.directory", [], true), Descriptor("data.convert", [new("value")], false),
            Descriptor("value.comment", [new("value"), new("displayName", EPortValueKind.Json)], true),
            Descriptor("data.concat", Enumerable.Range(1, ConcatPorts.Maximum).Select(index => new PortDescriptor(ConcatPorts.Name(index), EPortValueKind.Json)).ToArray(), true)];
        await model.LoadAsync(new(document, "base", DateTimeOffset.UtcNow, EActivationPolicy.Auto)).ConfigureAwait(true);
        EditorPresentationCache cache = new();
        Assert.Null(cache.Graph(model.Draft.Document, catalog).Port("comment", "value", true).InferredType);
        WireDefinition[] wires = [new("comment", "value", "concat", "input1"), new("concat", "value", "after", "value"),
            new("root", "value", "comment", "value"), new("leaf", "value", "concat", "input2")];
        foreach (WireDefinition wire in wires)
        {
            Assert.True(NodeInputEditing.Connect(model, wire, catalog), wire.ToString());
        }
        GraphTypeInference graph = cache.Graph(model.Draft.Document, catalog);
        foreach (string id in new[] { "root", "comment", "concat", "after" })
        {
            Assert.Equal("path", graph.Port(id, "value", true).Key);
            Assert.Equal("path", graph.Port(id, "value", true).InferredType);
        }
        NodeValue? preview = cache.Preview(model.Draft).Input(after, "value");
        bool dynamic = sourceType is "data.convert";
        if (dynamic)
        {
            Assert.Null(preview);
        }
        else
        {
            Assert.Equal("root/file.wav", preview!.GetString());
            Assert.Equal("path", preview.DataType!.Type);
        }
        Assert.Equal(3, graph.EditorDescriptor("concat")!.Inputs.Count);
    }

    /// <summary>Path同士のConcatを、通常のファイル設定と文字列入力へ接続できます。</summary>
    [Fact]
    public void PathConnectionsAndConcatPreviewPreserveTheirMeaning()
    {
        NodeDefinition first = new("root", "value.constant", JsonSerializer.SerializeToElement(new { type = "path", text = @"C:\content" }));
        NodeDefinition second = new("leaf", "value.constant", JsonSerializer.SerializeToElement(new { type = "path", text = "clip.wav" }));
        NodeDefinition concat = new("concat", "data.concat", JsonSerializer.SerializeToElement(new { }));
        NodeDefinition target = new("exists", "file.exists", JsonSerializer.SerializeToElement(new { path = "old" }));
        WorkflowDefinition definition = new("path-preview", "Path", [first, second, concat, target],
            [new("root", "value", "concat", "input1"), new("leaf", "value", "concat", "input2"), new("concat", "value", "exists", SettingPorts.Name("path"))]);
        NodeDescriptor[] catalog = [Descriptor("value.constant", [], true), Descriptor("data.concat", [new("input1"), new("input2")], true), Descriptor("file.exists", [], false)];
        Assert.All(definition.Wires, wire => Assert.True(WorkflowConnectionTypes.CanConnect(definition, catalog, wire)));
        GraphTypeInference graph = new(definition, catalog);
        Assert.Equal("path", graph.Port("root", "value", true).Key);
        Assert.Equal("path", graph.Port("concat", "value", true).InferredType);
        EditorDraft draft = new(definition, definition, "base", definition, null, "base", false, []);
        NodeValue preview = new EditorPresentationCache().Preview(draft).Input(target, SettingPorts.Name("path"))!;
        Assert.Equal("C:/content/clip.wav", preview.GetString());
        Assert.Equal("path", preview.DataType!.Type);
        NodeDefinition log = new("log", "control.log", JsonSerializer.SerializeToElement(new { }));
        NodeDescriptor logType = Descriptor("control.log", [new("message", EPortValueKind.Json)], false);
        Assert.True(WorkflowConnectionTypes.CanConnect(definition with { Nodes = [.. definition.Nodes, log] }, [.. catalog, logType], new("concat", "value", "log", "message")));
    }

    /// <summary>固定Path入力の編集で型を失わず、不正な文字は編集中エラーとして残します。</summary>
    /// <returns>実編集モデルの変更と拒否です。</returns>
    [Fact]
    public async Task EditingAPathInputRetainsItsTypeAndRejectsInvalidNames()
    {
        BrowserTestRuntime runtime = new();
        EditorDocument model = new(new(runtime));
        NodeDefinition node = new("concat", "data.concat", JsonSerializer.SerializeToElement(new { }),
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["input1"] = PathValues.Create("folder/old") });
        WorkflowDefinition definition = new("edit-path", "Path", [node], []);
        await model.LoadAsync(new(definition, "base", DateTimeOffset.UtcNow, EActivationPolicy.Auto)).ConfigureAwait(true);
        NodeInputEditing.Commit(model, node.Id, "input1", @"folder\new", false, JsonValueKind.String);
        NodeValue value = model.Draft.Document.Nodes[0].Inputs!["input1"];
        Assert.Equal("folder/new", value.GetString());
        Assert.Equal("path", value.DataType!.Type);
        NodeInputEditing.Commit(model, node.Id, "input1", "bad*file", false, JsonValueKind.String);
        Assert.True(model.HasInvalidInputs);
        Assert.Equal("folder/new", model.Draft.Document.Nodes[0].Inputs!["input1"].GetString());
    }

    /// <summary>表示と型互換性に必要な宣言だけを用意します。</summary>
    /// <returns>設定パスまたは通常入力を持つカタログ項目です。</returns>
    private static NodeDescriptor Descriptor(string type, IReadOnlyList<PortDescriptor> inputs, bool pure)
        => new(type, type, "Values", type, ENodeKind.Action, EExecutionLocation.Independent, inputs, [new("value", EPortValueKind.Json)], ["success"],
            JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"path":{"type":"string","x-runtime-input":true}}}"""), CanEvaluateStatically: pure);
}
