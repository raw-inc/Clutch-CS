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

/// <summary>YAMLへの書き出しと再読み込みで、ワークフローの内容が変わらないことを検証します。</summary>
public sealed class WorkflowYamlExportTests
{
    private static readonly string[] yamlLikeStrings = ["true", "null", "001", "yes", "2026-01-01"];

    /// <summary>バイト列、型情報、有効状態を含む定義が、再読み込み後も一致することを確認します。</summary>
    /// <returns>元の定義と再読み込みした定義の比較。</returns>
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

    /// <summary>引用符や改行、特殊文字を含むキーが、再読み込み後も変わらないことを確認します。</summary>
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

    /// <summary>存在しない入力に型情報を指定した場合、読み込みを拒否することを確認します。</summary>
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

    /// <summary>数値で記述されたYAMLのキーを、従来どおり文字列のキーとして読み込めることを確認します。</summary>
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
