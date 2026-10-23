using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Loading;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Configuration;

/// <summary>YAMLの書出しと再読込みで、値と実行・編集の設定を維持します。</summary>
public sealed class WorkflowYamlExportTests
{
    private static readonly string[] yamlLikeStrings = ["true", "null", "001", "yes", "2026-01-01"];

    /// <summary>JSON値、バイト列、型情報と有効状態を同じ定義へ戻します。</summary>
    /// <returns>元定義を変更せず、契約全体が一致する確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompleteDefinitionRoundTrips(bool dashboard)
    {
        JsonElement settings = JsonSerializer.SerializeToElement(new { code = "var text = \"日本語\";\nreturn text;\n", values = yamlLikeStrings });
        using JsonDocument number = JsonDocument.Parse("""{"huge":1234567890123456789012345678901234567890,"fraction":0.12345678901234567890123456789,"exponent":1e1000}""");
        Dictionary<string, NodeValue> inputs = new(StringComparer.Ordinal)
        {
            ["value"] = NodeValue.FromJson(number.RootElement),
            ["path"] = NodeValue.FromObject("C:\\Content\\movie.mp4") with { DataType = new ValueTypeDefinition("path") },
            ["packet"] = NodeValue.FromBytes(new byte[] { 0, 1, 254, 255 }),
            ["empty"] = NodeValue.FromBytes(ReadOnlyMemory<byte>.Empty),
            ["nil"] = NodeValue.Null,
        };
        WorkflowDefinition expected = new("export-example", "設定と値", [
            new("source", "script.csharp", settings, inputs, Position: new(12.5, -4.25), StartOnLoad: true, ContractVersion: 2, LogicalName: "入力"),
            new("empty", "control.pass", JsonSerializer.SerializeToElement(new { }), new Dictionary<string, NodeValue>(StringComparer.Ordinal)),
            new("sink", "control.pass", JsonSerializer.SerializeToElement(new { })),
        ], [new("source", "value", "sink", "value", Path: "/fraction")], Enabled: true, Trace: true, ContainsPersonalData: true,
            ActivationRules: new Dictionary<EWorkflowChangeKind, EActivationPolicy> { [EWorkflowChangeKind.Values] = EActivationPolicy.Explicit })
        { IsDashboard = dashboard, IsRecipe = !dashboard };
        string before = ContractJson.Hash(expected);
        string yaml = WorkflowYamlWriter.Write(expected);
        WorkflowDefinition actual = WorkflowYamlReader.Read(yaml);
        Assert.Equal(before, ContractJson.Hash(expected));
        Assert.Equal(before, ContractJson.Hash(actual));
        Assert.Equal(new byte[] { 0, 1, 254, 255 }, actual.Nodes[0].Inputs!["packet"].Binary.ToArray());
    }

    /// <summary>引用・改行・特殊なキーを値の一部として保持します。</summary>
    [Theory]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("001")]
    [InlineData("a: b\n- child\n")]
    [InlineData("line\r\nnext\r\n")]
    [InlineData("&anchor *alias # comment")]
    public void StringsRemainStrings(string text)
    {
        WorkflowDefinition expected = new("true", text, [new("n", "control.pass", JsonSerializer.SerializeToElement(new { text }),
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["null"] = NodeValue.FromObject(text) })], []);
        Assert.Equal(ContractJson.Hash(expected), ContractJson.Hash(WorkflowYamlReader.Read(WorkflowYamlWriter.Write(expected))));
    }

    /// <summary>存在しない入力へ型情報を適用した不正な定義を受け付けません。</summary>
    [Fact]
    public void OrphanedInputTypesAreRejected()
    {
        Assert.Throws<FormatException>(() => WorkflowYamlReader.Read("""
            id: example
            nodes:
              - id: pass
                type: control.pass
                inputTypes:
                  absent:
                    type: path
            """));
    }

    /// <summary>従来のYAMLで使える数値のマッピングキーも文字列キーへ引き継ぎます。</summary>
    [Fact]
    public void NumericMappingKeysRemainReadable()
    {
        WorkflowDefinition value = WorkflowYamlReader.Read("""
            id: numeric-keys
            nodes:
              - id: pass
                type: control.pass
                inputs:
                  value:
                    12: twelve
            """);
        Assert.Equal("twelve", value.Nodes[0].Inputs!["value"].Json.GetProperty("12").GetString());
    }
}
