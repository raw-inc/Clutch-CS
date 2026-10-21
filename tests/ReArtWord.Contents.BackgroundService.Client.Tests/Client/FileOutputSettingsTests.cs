using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Inspection;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>旧上書き禁止の保存値を、画面だけで上書き可能へ読み替えません。</summary>
public sealed class FileOutputSettingsTests
{
    /// <summary>未指定・旧指定・新指定の優先順位を編集用の表示にも反映します。</summary>
    [Theory]
    [InlineData("{}", "overwrite")]
    [InlineData("{\"overwrite\":false}", "error")]
    [InlineData("{\"overwrite\":true}", "overwrite")]
    [InlineData("{\"overwrite\":false,\"existingFileMode\":\"skip\"}", "overwrite")]
    public void EditorPreservesExistingOverwriteChoice(string settings, string expectedDefault)
    {
        JsonElement schema = JsonSerializer.Deserialize<JsonElement>("""{"properties":{"existingFileMode":{"type":"string","enum":["overwrite","skip","error"],"default":"overwrite"},"overwrite":{"type":"boolean","x-legacy":true}}}""");
        NodeDefinition node = new("copy", "file.copy", JsonSerializer.Deserialize<JsonElement>(settings));
        NodeDescriptor descriptor = new(node.Type, node.Type, "Files", "", ENodeKind.Action, EExecutionLocation.ExplicitPeer, [], [], [], schema);
        WorkflowDefinition workflow = new("flow", "flow", [node], []);
        JsonElement editable = EditorNodeDescriptor.Resolve(descriptor, node, workflow)!.SettingsSchema.GetProperty("properties");
        Assert.Equal(expectedDefault, editable.GetProperty("existingFileMode").GetProperty("default").GetString());
        Assert.Equal(settings, node.Settings.GetRawText());
        Assert.Equal("overwrite", schema.GetProperty("properties").GetProperty("existingFileMode").GetProperty("default").GetString());
        Assert.Equal(node.Settings.TryGetProperty("overwrite", out _), editable.TryGetProperty("overwrite", out _));
    }
}
