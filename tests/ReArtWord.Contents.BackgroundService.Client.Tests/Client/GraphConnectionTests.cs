using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>配線の型判定と確定前の保存値保護を検証します。</summary>
public sealed class GraphConnectionTests
{
    /// <summary>既知の値区分だけを拒否し、ANYやtriggerを狭めません。</summary>
    [Theory]
    [InlineData(EPortValueKind.Binary, EPortValueKind.Json, "value", false)]
    [InlineData(EPortValueKind.Json, EPortValueKind.Binary, "value", false)]
    [InlineData(EPortValueKind.Binary, EPortValueKind.Any, "value", true)]
    [InlineData(EPortValueKind.Any, EPortValueKind.Json, "value", true)]
    [InlineData(EPortValueKind.Binary, EPortValueKind.Json, "trigger", true)]
    public void DeclaredAcceptanceDoesNotGuessUnknown(EPortValueKind output, EPortValueKind input, string port, bool expected)
    {
        WorkflowDefinition document = Document();
        Assert.Equal(expected, GraphConnectionPolicy.CanConnect(document, [Descriptor("from", output), Descriptor("to", input)], new("a", "value", "b", port)));
    }

    /// <summary>空Pointerは全体値を保持し、バイト列内部の参照を許可しません。</summary>
    [Theory]
    [InlineData(EPortValueKind.Binary, EPortValueKind.Binary, null, true)]
    [InlineData(EPortValueKind.Binary, EPortValueKind.Binary, "", true)]
    [InlineData(EPortValueKind.Binary, EPortValueKind.Json, null, false)]
    [InlineData(EPortValueKind.Binary, EPortValueKind.Json, "", false)]
    [InlineData(EPortValueKind.Binary, EPortValueKind.Binary, "/value", false)]
    [InlineData(EPortValueKind.Binary, EPortValueKind.Json, "/value", false)]
    [InlineData(EPortValueKind.Json, EPortValueKind.Json, "/value", true)]
    [InlineData(EPortValueKind.Json, EPortValueKind.Binary, "/value", false)]
    [InlineData(EPortValueKind.Any, EPortValueKind.Binary, "/value", false)]
    [InlineData(EPortValueKind.Any, EPortValueKind.Json, "/value", true)]
    public void PointerPreservesWholeValueKind(EPortValueKind output, EPortValueKind input, string? path, bool expected)
    {
        Assert.Equal(expected, GraphConnectionPolicy.CanConnect(Document(), [Descriptor("from", output), Descriptor("to", input)], new("a", "value", "b", "value", Path: path)));
    }

    /// <summary>共通trigger入力でも実行不能なBinary内部参照を受け入れません。</summary>
    [Fact]
    public void TriggerCannotHideInvalidBinaryExtraction()
    {
        Assert.False(GraphConnectionPolicy.CanConnect(Document(), [Descriptor("from", EPortValueKind.Binary), Descriptor("to", EPortValueKind.Any)], new("a", "value", "b", "trigger", Path: "/value")));
    }

    /// <summary>占有ANY入力の旧JSON型で新Binary線を拒否せず、一回で元線へ戻せます。</summary>
    /// <returns>編集境界の検証です。</returns>
    [Fact]
    public async Task ReplacementAndRejectedDropPreserveOriginalAsync()
    {
        EditorDocument model = new(new(new BrowserTestRuntime()));
        WorkflowDefinition document = Document() with { Wires = [new("c", "value", "b", "value")] };
        await model.LoadAsync(new(document, "base", DateTimeOffset.UtcNow, EActivationPolicy.Auto)).ConfigureAwait(true);
        NodeDescriptor[] catalog = [Descriptor("from", EPortValueKind.Binary), Descriptor("to", EPortValueKind.Any)];
        model.SetInput("b/settings/other", "unfinished");
        Assert.False(NodeInputEditing.Connect(model, new("a", "missing", "b", "value"), catalog));
        Assert.Equal(document, model.Draft.Document);
        Assert.False(model.CanUndo);
        model.Locks.Update([new(Guid.NewGuid(), "flow", ["c"])]);
        Assert.False(NodeInputEditing.CanConnect(model, new("a", "value", "b", "value"), catalog));
        Assert.False(NodeInputEditing.Connect(model, new("a", "value", "b", "value"), catalog));
        model.Locks.Update([]);
        Assert.True(NodeInputEditing.CanConnect(model, new("a", "value", "b", "value"), catalog));
        Assert.Equal(document, model.Draft.Document);
        Assert.False(model.CanUndo);
        Assert.True(NodeInputEditing.Connect(model, new("a", "value", "b", "value"), catalog));
        Assert.Equal("a", Assert.Single(model.Draft.Document.Wires).FromNode);
        Assert.Equal("unfinished", model.ReadInput("b/settings/other"));
        model.Undo();
        Assert.Equal("c", Assert.Single(model.Draft.Document.Wires).FromNode);
    }

    /// <summary>設定スキーマに対する安全な出力型推定を用い、元設定は書換えません。</summary>
    [Theory]
    [InlineData("string", true)]
    [InlineData("number", false)]
    public void SettingsUseSchemaInsteadOfOldValue(string type, bool expected)
    {
        WorkflowDefinition document = Document() with { Nodes = [new("a", "value.interface", JsonSerializer.SerializeToElement(new { })), Document().Nodes[1]] };
        NodeDescriptor target = Descriptor("to", EPortValueKind.Any) with { SettingsSchema = JsonSerializer.SerializeToElement(new { properties = new { interfaceId = new { type } } }) };
        Assert.Equal(expected, GraphConnectionPolicy.CanConnect(document, [Descriptor("value.interface", EPortValueKind.Any), target], new("a", "value", "b", "$settings.interfaceId")));
    }

    /// <summary>ドラッグ中の外部編集やUndo後へ古い確定イベントを適用しません。</summary>
    /// <returns>草稿同一性の検証です。</returns>
    [Fact]
    public async Task ChangedDraftRejectsLateDropWithoutLosingCurrentStateAsync()
    {
        EditorDocument model = new(new(new BrowserTestRuntime()));
        WorkflowDefinition original = Document();
        await model.LoadAsync(new(original, "base", DateTimeOffset.UtcNow, EActivationPolicy.Auto)).ConfigureAwait(true);
        model.ChangeNode("b", node => node with { Settings = JsonSerializer.SerializeToElement(new { value = "new" }) });
        model.SetInput("b/inputs/value", "unfinished");
        Assert.False(NodeInputEditing.CanConnect(model, new("a", "value", "b", "value"), [], original));
        Assert.False(NodeInputEditing.Connect(model, new("a", "value", "b", "value"), [], original));
        Assert.Empty(model.Draft.Document.Wires);
        Assert.Equal("unfinished", model.ReadInput("b/inputs/value"));
        Assert.Equal("new", model.Draft.Document.Nodes[1].Settings.GetProperty("value").GetString());
        model.Undo();
        Assert.Equal(original.Nodes[1].Settings.GetRawText(), model.Draft.Document.Nodes[1].Settings.GetRawText());
    }

    /// <summary>旧setting接続と未加工のの不正入力も、不適合dropでは失いません。</summary>
    /// <returns>確定前拒否の検証です。</returns>
    [Fact]
    public async Task IncompatibleSettingReplacementPreservesWireAndRawAsync()
    {
        EditorDocument model = new(new(new BrowserTestRuntime()));
        WorkflowDefinition original = Document() with { Wires = [new("c", "value", "b", "$settings.interfaceId")] };
        await model.LoadAsync(new(original, "base", DateTimeOffset.UtcNow, EActivationPolicy.Auto)).ConfigureAwait(true);
        model.SetInput("b/settings/interfaceId", "unfinished");
        NodeDescriptor target = Descriptor("to", EPortValueKind.Any) with { SettingsSchema = JsonSerializer.SerializeToElement(new { properties = new { interfaceId = new { type = "string" } } }) };
        Assert.False(NodeInputEditing.CanConnect(model, new("a", "value", "b", "$settings.interfaceId"), [Descriptor("from", EPortValueKind.Binary), target]));
        Assert.False(NodeInputEditing.Connect(model, new("a", "value", "b", "$settings.interfaceId"), [Descriptor("from", EPortValueKind.Binary), target]));
        Assert.Equal("c", Assert.Single(model.Draft.Document.Wires).FromNode);
        Assert.Equal("unfinished", model.ReadInput("b/settings/interfaceId"));
        Assert.False(model.CanUndo);
    }

    /// <summary>小さな未実行草稿を作ります。</summary>
    /// <returns>接続対象です。</returns>
    private static WorkflowDefinition Document() => new("flow", "flow", [new("a", "from", JsonSerializer.SerializeToElement(new { })), new("b", "to", JsonSerializer.SerializeToElement(new { })), new("c", "from", JsonSerializer.SerializeToElement(new { }))], []);

    /// <summary>ポートの宣言だけを使う試験用の構成です。</summary>
    /// <returns>型契約です。</returns>
    private static NodeDescriptor Descriptor(string type, EPortValueKind kind) => new(type, type, "Data", "", ENodeKind.Action, EExecutionLocation.Independent, [new("value", kind)], [new("value", kind)], ["success"], JsonSerializer.SerializeToElement(new { }));
}
