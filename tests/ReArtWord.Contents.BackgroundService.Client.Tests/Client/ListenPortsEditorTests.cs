using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>待受ポートをJSONの引用符なしで編集し、配列と既存の型入力も維持します。</summary>
public sealed class ListenPortsEditorTests
{
    /// <summary>カンマや範囲を通常入力で受け付け、数値・配列の保存型も維持します。</summary>
    [Theory]
    [InlineData("9000", "9000")]
    [InlineData("9000-9005", "\"9000-9005\"")]
    [InlineData("9000, 9002, 9006-9010", "\"9000, 9002, 9006-9010\"")]
    [InlineData("[9000,9002]", "[9000,9002]")]
    public void PortTextCanBeSavedWithoutJsonQuoting(string raw, string expected)
    {
        string editor = SettingChoiceValues.EditorType(ListenPorts.Schema);
        Assert.Equal("listen-ports", editor);
        JsonElement value = JsonSerializer.SerializeToElement(SettingInputValues.Parse(raw, editor));
        Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(expected), value));
        Assert.NotEmpty(ListenPorts.Read(value));
    }

    /// <summary>編集中の不正な指定を保存値へ反映しません。</summary>
    [Theory]
    [InlineData("9005-9000")]
    [InlineData("[9000,\"9001\"]")]
    [InlineData("9000,")]
    [InlineData("true")]
    [InlineData("0,9000")]
    public void PortEditorRejectsInvalidSelections(string raw)
        => Assert.Throws<FormatException>(() => SettingInputValues.Parse(raw, "listen-ports"));

    /// <summary>既存の一般設定欄の型変換は維持します。</summary>
    [Theory]
    [InlineData("string", "a,b", "\"a,b\"")]
    [InlineData("integer", "42", "42")]
    [InlineData("number", "0.25", "0.25")]
    [InlineData("boolean", "false", "false")]
    [InlineData("array", "[1,2]", "[1,2]")]
    [InlineData("object", "{\"a\":1}", "{\"a\":1}")]
    [InlineData("json", "null", "null")]
    public void OtherSettingEditorsKeepTheirValueTypes(string type, string raw, string expected)
        => Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(expected), JsonSerializer.SerializeToElement(SettingInputValues.Parse(raw, type))));
}
