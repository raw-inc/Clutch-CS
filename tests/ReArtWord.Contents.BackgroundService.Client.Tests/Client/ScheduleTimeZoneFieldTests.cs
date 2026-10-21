using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Inspection;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>旧日時ノードに、明示されていないUTC設定を表示・保存しません。</summary>
public sealed class ScheduleTimeZoneFieldTests
{
    /// <summary>新規配置のUTC既定を維持し、配置済みの旧未指定だけ共通設定を表す空欄にします。</summary>
    [Theory]
    [InlineData("{\"mode\":\"daily\",\"time\":\"09:00\"}", "")]
    [InlineData("{\"mode\":\"daily\",\"time\":\"09:00\",\"timeZoneId\":\"Asia/Tokyo\"}", "UTC")]
    public void LegacyUnspecifiedZoneDoesNotDisplayUtc(string settings, string expected)
    {
        JsonElement schema = JsonSerializer.Deserialize<JsonElement>("""{"properties":{"timeZoneId":{"type":"string","default":"UTC"}}}""");
        NodeDefinition node = new("timer", "scheduling.timer", JsonSerializer.Deserialize<JsonElement>(settings));
        NodeDescriptor descriptor = new(node.Type, node.Type, "Scheduling", "", ENodeKind.Source, EExecutionLocation.ExplicitPeer, [], [], [], schema);
        WorkflowDefinition workflow = new("flow", "flow", [node], []);
        JsonElement editable = EditorNodeDescriptor.Resolve(descriptor, node, workflow)!.SettingsSchema.GetProperty("properties");
        Assert.Equal(expected, editable.GetProperty("timeZoneId").GetProperty("default").GetString());
        Assert.Equal("UTC", schema.GetProperty("properties").GetProperty("timeZoneId").GetProperty("default").GetString());
        Assert.Equal(settings, node.Settings.GetRawText());
    }
}
