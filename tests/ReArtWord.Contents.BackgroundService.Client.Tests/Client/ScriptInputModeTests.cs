using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Inspection;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>表示・配線・固定値を、選択した入力方式へ一貫して反映します。</summary>
public sealed class ScriptInputModeTests
{
    /// <summary>全言語で同じ解析結果から引数名と型を表示し、旧方式の口を混在させません。</summary>
    [Theory]
    [InlineData("csharp")]
    [InlineData("python")]
    [InlineData("javascript")]
    public void ArgumentsBecomeExclusiveTypedPorts(string language)
    {
        ScriptContract contract = new(new Dictionary<string, ValueTypeDefinition>(StringComparer.Ordinal) { ["a"] = new("integer"), ["b"] = new("number"), ["c"] = new("string"), ["trigger"] = new("any") }, new("string"));
        ScriptAnalysis analysis = new(ContractJson.Hash(new ScriptAnalysisRequest(language, "source")), contract, []);
        NodeDefinition node = new("script", "script." + language, JsonSerializer.SerializeToElement(new { code = "source", analysis, inputMode = "individual" }, ContractJson.Options));
        NodeDescriptor descriptor = new(node.Type, "Script", "Scripting", "Script", ENodeKind.Action, EExecutionLocation.ExplicitPeer, [new("arguments", EPortValueKind.Json)], [new("value")], [], JsonSerializer.SerializeToElement(new { }));
        NodeDescriptor display = EditorNodeDescriptor.Resolve(descriptor, node, new("flow", "Flow", [node], []))!;
        Assert.Equal(["$arguments.a", "$arguments.b", "$arguments.c", "$arguments.trigger"], display.Inputs.Select(port => port.Name));
        Assert.Equal(["integer", "number", "string", "any"], display.Inputs.Select(port => port.DataType!.Type));
        Assert.Equal("trigger", new UiText(new(new BrowserTestRuntime())).PortName(node.Type, "$arguments.trigger", false));
        NodeDefinition grouped = ScriptInputMode.Change(node, false);
        Assert.Equal("arguments", Assert.Single(EditorNodeDescriptor.Resolve(descriptor, grouped, new("flow", "Flow", [grouped], []))!.Inputs).Name);
        Assert.Null(ScriptSettings.PortType(grouped, "$arguments.a", false));
    }

    /// <summary>固定値・Path型・共通開始入力を切替の往復で保持します。</summary>
    [Fact]
    public void SwitchingPreservesConstantsAndTheirPathMetadata()
    {
        NodeValue value = NodeValue.FromObject(new { a = 3, file = "folder/clip.wav" }) with { DataType = new("object", Properties: new Dictionary<string, ValueTypeDefinition>(StringComparer.Ordinal) { ["file"] = new("path") }) };
        NodeDefinition original = new("script", "script.csharp", JsonSerializer.SerializeToElement(new { code = "source" }), new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["arguments"] = value, ["trigger"] = NodeValue.Null });
        NodeDefinition individual = ScriptInputMode.Change(original, true);
        Assert.False(individual.Inputs!.ContainsKey("arguments"));
        Assert.Equal(3, individual.Inputs["$arguments.a"].Json.GetInt32());
        Assert.Equal("path", individual.Inputs["$arguments.file"].DataType!.Type);
        NodeDefinition grouped = ScriptInputMode.Change(individual, false);
        Assert.True(JsonElement.DeepEquals(value.Json, grouped.Inputs!["arguments"].Json));
        Assert.Equal("path", grouped.Inputs["arguments"].DataType!.Properties!["file"].Type);
        Assert.True(grouped.Inputs.ContainsKey("trigger"));
        Assert.True(original.Inputs!.ContainsKey("arguments"));
    }

    /// <summary>引数の配線は利用者が外し、開始信号だけなら切替を許可します。</summary>
    [Theory]
    [InlineData("arguments", false)]
    [InlineData("$arguments.a", false)]
    [InlineData("trigger", true)]
    public void SwitchingRequiresArgumentWiresToBeDisconnected(string port, bool allowed)
    {
        WorkflowDefinition document = new("flow", "Flow", [], [new("source", "value", "script", port)]);
        Assert.Equal(allowed, ScriptInputMode.CanChange(document, "script"));
    }
}
