using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Scripting;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Database;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Values;

/// <summary>説明・コードノードで元の値を変更したり、編集時にコードを実行したりしません。</summary>
public sealed class EditorValueNodeTests
{
    /// <summary>全表示言語が保存検証を通り、コードを実行せず元の文字列を返します。</summary>
    [Theory]
    [InlineData("text")]
    [InlineData("csharp")]
    [InlineData("python")]
    [InlineData("javascript")]
    [InlineData("bash")]
    [InlineData("powershell")]
    [InlineData("json")]
    [InlineData("toml")]
    [InlineData("yaml")]
    public void HighlightLanguagesPreserveCodeAndPassValidation(string language)
    {
        CodeBlockNode node = new();
        string code = "日本語 <tag> & \"quoted\"\r\n${value}\n";
        NodeDefinition definition = new("code", node.Descriptor.Type, JsonSerializer.SerializeToElement(new { language, code }));
        Assert.Contains(language, CodeBlockLanguages.All);
        Assert.Empty(NodeSchemaValidator.Validate(definition, node.Descriptor));
        Assert.Equal(code, node.Evaluate(definition, new Dictionary<string, NodeValue>(StringComparer.Ordinal)).Outputs["value"].GetString());
        Assert.Equal(language, ScriptSettings.Request(definition.Type, definition.Settings).Language);
    }

    /// <summary>言語が未指定の既存定義を維持し、誤った言語の保存は拒否します。</summary>
    [Fact]
    public void MissingLanguageRemainsCompatibleAndUnknownLanguageIsRejected()
    {
        CodeBlockNode node = new();
        NodeDefinition legacy = new("code", node.Descriptor.Type, JsonSerializer.SerializeToElement(new { code = "saved" }));
        Assert.Empty(NodeSchemaValidator.Validate(legacy, node.Descriptor));
        Assert.NotEmpty(NodeSchemaValidator.Validate(legacy with { Settings = JsonSerializer.SerializeToElement(new { code = "saved", language = "unknown" }) }, node.Descriptor));
    }

    /// <summary>バイナリとJSONを同一値で通し、コメント欄には設定入力を公開しません。</summary>
    [Fact]
    public void CommentPreservesValueAndHasNoCommentInput()
    {
        CommentNode node = new();
        NodeDefinition definition = new("comment", node.Descriptor.Type, JsonSerializer.SerializeToElement(new { comment = "説明" }));
        foreach (NodeValue value in new[] { NodeValue.FromObject(42), NodeValue.FromBytes(new byte[] { 0, 255, 3 }) })
        {
            NodeResult result = node.Evaluate(definition, new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = value, ["displayName"] = NodeValue.FromObject("age") });
            NodeValue actual = result.Outputs["value"];
            Assert.Equal(value.Kind, actual.Kind);
            if (value.Kind == EValueKind.Json)
            {
                Assert.True(JsonElement.DeepEquals(value.Json, actual.Json));
            }
            else
            {
                Assert.Equal(value.Binary.ToArray(), actual.Binary.ToArray());
            }
        }
        Assert.DoesNotContain(node.Descriptor.Inputs, port => string.Equals(port.Name, "comment", StringComparison.Ordinal));
        Assert.False(SettingPorts.IsDeclared(node.Descriptor, SettingPorts.Name("comment")));
        Assert.Contains(node.Descriptor.Inputs, port => string.Equals(port.Name, "displayName", StringComparison.Ordinal));
        Assert.DoesNotContain(node.Descriptor.SettingsSchema.GetProperty("properties").EnumerateObject(), property => SettingPorts.CanConnect(property.Value));
        Assert.Equal("value.displayName.invalid", node.Evaluate(definition, new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(1), ["displayName"] = NodeValue.FromObject(2) }).ReasonCode);
    }

    /// <summary>不正な構文もテキストとして保ち、構文検査は別の結果として返します。</summary>
    /// <returns>実行を伴わない編集処理です。</returns>
    [Fact]
    public async Task CodeBlockReturnsExactTextAndChecksSyntaxWithoutEntryPointAsync()
    {
        CodeBlockNode node = new();
        string code = "// comment\nthrow new Exception(\"must never run\");";
        NodeDefinition definition = new("code", node.Descriptor.Type, JsonSerializer.SerializeToElement(new { language = "csharp", code }));
        Assert.Empty(node.Descriptor.Inputs);
        Assert.Equal(code, node.Evaluate(definition, new Dictionary<string, NodeValue>(StringComparer.Ordinal)).Outputs["value"].GetString());
        ScriptAnalysisRequest request = ScriptSettings.Request(definition.Type, definition.Settings);
        Assert.True(request.SyntaxOnly);
        ScriptAnalysis valid = await ScriptAnalysisService.AnalyzeAsync(request).ConfigureAwait(true);
        Assert.Empty(valid.Diagnostics);
        Assert.Null(valid.Contract);
        Assert.Equal(ContractJson.Hash(request), valid.Fingerprint);
        Assert.NotEmpty((await ScriptAnalysisService.AnalyzeAsync(request with { Code = "int n = ;" }).ConfigureAwait(true)).Diagnostics);
    }

    /// <summary>Python構文検査ではトップレベルコードも型推定も実行しません。</summary>
    /// <returns>有効・無効な構文の判別です。</returns>
    [ExternalRuntimeFact("CONTENTS_TEST_PYTHON")]
    public async Task PythonCodeBlockSyntaxDoesNotRequireRunOrTypesAsync()
    {
        ScriptAnalysisRequest request = new("python", "raise RuntimeError('must never run')", PythonPath: Environment.GetEnvironmentVariable("CONTENTS_TEST_PYTHON"), SyntaxOnly: true);
        Assert.Empty((await ScriptAnalysisService.AnalyzeAsync(request).ConfigureAwait(true)).Diagnostics);
        Assert.NotEmpty((await ScriptAnalysisService.AnalyzeAsync(request with { Code = "def broken(:" }).ConfigureAwait(true)).Diagnostics);
    }
}
