using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>二分岐Signalとnull補完を、編集画面の接続型・表示名にも反映します。</summary>
public sealed class ConditionalNodesEditorTests
{
    /// <summary>任意型の入力を受けるIsNullの結果を、通常の真偽値として条件へ接続できます。</summary>
    /// <returns>日本語・英語の表示名と、null判定からIfへの型付き接続です。</returns>
    [Theory]
    [InlineData("ja", "入力値", "null判定")]
    [InlineData("en", "Input value", "Is null")]
    public async Task IsNullExposesBooleanDataAsync(string language, string inputLabel, string outputLabel)
    {
        JsonElement empty = JsonSerializer.SerializeToElement(new { });
        NodeDescriptor check = new("data.isNull", "IsNull", "Data", "", ENodeKind.Action, EExecutionLocation.Independent,
            [new("value", Required: true)], [new("value", EPortValueKind.Json) { DataType = new("boolean") }], ["success"], empty);
        NodeDescriptor branch = new("control.if", "If", "Control", "", ENodeKind.Action, EExecutionLocation.Independent,
            [new("condition", EPortValueKind.Json, true)], [new("value", EPortValueKind.Json)], ["success"], empty);
        NodeDescriptor source = new("test.binary", "Binary", "Test", "", ENodeKind.Action, EExecutionLocation.Independent,
            [], [new("value", EPortValueKind.Binary)], ["success"], empty);
        WorkflowDefinition definition = new("is-null-editor", "IsNull editor", [new("source", source.Type, empty), new("check", check.Type, empty), new("if", branch.Type, empty)], []);
        NodeDescriptor[] catalog = [source, check, branch];
        Assert.True(GraphConnectionPolicy.CanConnect(definition, catalog, new("source", "value", "check", "value")));
        Assert.True(GraphConnectionPolicy.CanConnect(definition, catalog, new("check", "value", "if", "condition")));
        Assert.Equal("boolean", new GraphTypeInference(definition, catalog).Port("check", "value", true).Key);
        UiText ui = new(new(new BrowserTestRuntime()));
        await ui.ChangeAsync(language).ConfigureAwait(true);
        Assert.Equal("IsNull", ui.NodeName(check));
        Assert.Equal(inputLabel, ui.PortName(check.Type, "value", false));
        Assert.Equal(outputLabel, ui.PortName(check.Type, "value", true));
    }

    /// <summary>二つの開始出力だけをSignalとして接続し、判定値は通常の真偽値に保ちます。</summary>
    /// <returns>日本語・英語のポート名と、Signal合流への接続の確認です。</returns>
    [Theory]
    [InlineData("ja", "True側 開始Signal", "False側 開始Signal", "比較真偽値")]
    [InlineData("en", "True start signal", "False start signal", "Condition value")]
    public async Task IfPortsDistinguishSignalsFromBooleanDataAsync(string language, string trueLabel, string falseLabel, string valueLabel)
    {
        JsonElement empty = JsonSerializer.SerializeToElement(new { });
        NodeDescriptor branch = new("control.if", "If", "Control", "", ENodeKind.Action, EExecutionLocation.Independent,
            [new("condition", EPortValueKind.Json, true)], [new("trueStart", EPortValueKind.Json), new("falseStart", EPortValueKind.Json), new("value", EPortValueKind.Json)], ["success"], empty);
        NodeDescriptor merge = new("control.signal.each", "Merge", "Control", "", ENodeKind.Action, EExecutionLocation.Independent, [new("input1")], [new("start")], ["success"], empty);
        WorkflowDefinition definition = new("if-editor", "If editor", [new("if", branch.Type, empty), new("merge", merge.Type, empty)], []);
        NodeDescriptor[] catalog = [branch, merge];
        Assert.True(GraphConnectionPolicy.CanConnect(definition, catalog, new("if", "trueStart", "merge", "input1")));
        Assert.True(GraphConnectionPolicy.CanConnect(definition, catalog, new("if", "falseStart", "merge", "input1")));
        Assert.False(GraphConnectionPolicy.CanConnect(definition, catalog, new("if", "value", "merge", "input1")));
        GraphTypeInference graph = new(definition, catalog);
        Assert.Equal("boolean", graph.Port("if", "condition", false).Key);
        Assert.Equal("boolean", graph.Port("if", "value", true).Key);
        UiText ui = new(new(new BrowserTestRuntime()));
        await ui.ChangeAsync(language).ConfigureAwait(true);
        Assert.Equal(trueLabel, ui.PortName(branch.Type, "trueStart", true));
        Assert.Equal(falseLabel, ui.PortName(branch.Type, "falseStart", true));
        Assert.Equal(valueLabel, ui.PortName(branch.Type, "value", true));
    }

    /// <summary>nullableな入力に同型の既定値を与えると、出力からnull許容だけを取り除きます。</summary>
    [Theory]
    [InlineData("string", "fallback", "string", false)]
    [InlineData("integer", 42, "integer", false)]
    [InlineData("string", 42, "any", false)]
    public void DefaultOutputReflectsBothPossibleTypes(string inputType, object fallback, string expected, bool optional)
    {
        JsonElement empty = JsonSerializer.SerializeToElement(new { });
        NodeDescriptor source = new("test.optional", "Optional", "Test", "", ENodeKind.Action, EExecutionLocation.Independent, [],
            [new("value") { DataType = new(inputType, Optional: true) }], ["success"], empty);
        NodeDescriptor choice = new("data.defaultIfNull", "Null → Default", "Data", "", ENodeKind.Action, EExecutionLocation.Independent,
            [new("value", Required: true), new("defaultValue")], [new("value")], ["success"], empty, CanEvaluateStatically: true);
        WorkflowDefinition definition = new("default-editor", "Default editor", [new("source", source.Type, empty),
            new("default", choice.Type, empty, new Dictionary<string, NodeValue> { ["defaultValue"] = NodeValue.FromObject(fallback) })], [new("source", "value", "default", "value")]);
        GraphPortPresentation actual = new GraphTypeInference(definition, [source, choice]).Port("default", "value", true);
        Assert.Equal(expected, actual.DataType?.Type ?? actual.Key);
        Assert.Equal(optional, actual.DataType?.Optional ?? false);
    }
}
