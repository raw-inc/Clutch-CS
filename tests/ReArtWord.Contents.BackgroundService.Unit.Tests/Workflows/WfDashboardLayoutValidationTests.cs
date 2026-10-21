using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow.Dashboards;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>コメントと改行を保存用の配置に限定し、実行可能な設定を拒否します。</summary>
public sealed class WfDashboardLayoutValidationTests
{
    /// <summary>表示専用ノードの型と設定を検証し、実行順へ混ぜません。</summary>
    [Theory]
    [InlineData("dashboard.comment", "{\"text\":\"first\\nsecond\"}", true)]
    [InlineData("dashboard.comment", "{\"text\":\"\"}", true)]
    [InlineData("dashboard.comment", "{\"text\":null}", false)]
    [InlineData("dashboard.comment", "{\"text\":12}", false)]
    [InlineData("dashboard.comment", "{}", false)]
    [InlineData("dashboard.break", "{}", true)]
    [InlineData("dashboard.break", "null", false)]
    [InlineData("dashboard.break", "{\"text\":\"ignored\"}", false)]
    [InlineData("dashboard.state", "{\"target\":{\"workflowId\":\"demo\",\"scope\":\"shared\",\"key\":\"cue\"}}", true)]
    [InlineData("dashboard.state", "{\"target\":{\"workflowId\":\"\",\"scope\":\"cluster\",\"key\":\"cue\"}}", true)]
    [InlineData("dashboard.state", "{\"target\":{\"workflowId\":\"\",\"scope\":\"shared\",\"key\":\"cue\"}}", false)]
    [InlineData("dashboard.state", "{\"target\":{\"workflowId\":\"demo\",\"scope\":\"cluster\",\"key\":\"cue\"}}", false)]
    [InlineData("dashboard.state", "{\"target\":null}", false)]
    public void ValidatesLayoutOnlySettings(string type, string json, bool valid)
    {
        using JsonDocument settings = JsonDocument.Parse(json);
        NodeDefinition node = new("local", type, settings.RootElement.Clone());
        WorkflowDefinition definition = new("dashboard", "Dashboard", [node], []) { IsDashboard = true };
        WorkflowCompiler compiler = new(new([]));
        CompilationResult result = compiler.Compile(definition);
        Assert.Equal(valid, result.Workflow is not null);
        if (valid)
        {
            Assert.Empty(result.Workflow!.OrderedNodes);
            Assert.Null(compiler.Compile(definition with { Nodes = [node with { StartOnLoad = true }] }).Workflow);
            Assert.Null(compiler.Compile(definition with { Nodes = [node with { Inputs = new Dictionary<string, NodeValue> { ["trigger"] = NodeValue.FromObject(true) } }] }).Workflow);
        }
        Assert.Null(compiler.Compile(definition with { IsDashboard = false }).Workflow);
    }

    /// <summary>保持上限を超える本文は切捨てず、保存を拒否します。</summary>
    [Fact]
    public void RejectsOversizedCommentsWithoutTruncation()
    {
        string maximum = new('x', WfDashboardLayout.MaximumCommentLength);
        Assert.True(WfDashboardLayout.TryReadComment(WfDashboardLayout.CommentSettings(maximum), out string restored));
        Assert.Equal(maximum, restored);
        Assert.Throws<ArgumentOutOfRangeException>(() => WfDashboardLayout.CommentSettings(maximum + "x"));
        JsonElement oversized = JsonSerializer.SerializeToElement(new { text = maximum + "x" });
        WorkflowCompiler compiler = new(new([]));
        CompilationResult result = compiler.Compile(new("dashboard", "Dashboard", [new("comment", WfDashboardLayout.CommentType, oversized)], []) { IsDashboard = true });
        Assert.Contains(result.Issues, issue => issue.Code is "dashboard.comment");
    }
}
