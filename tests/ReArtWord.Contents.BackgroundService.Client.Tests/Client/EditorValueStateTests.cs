using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>処理中nullと成否表示が、別WFの更新や未保存草稿へ混ざらないことを確認します。</summary>
public sealed class EditorValueStateTests
{
    /// <summary>追加の実行要求をせず、観測からnull・true・falseを表示します。</summary>
    /// <returns>状態表示と描画版の変化です。</returns>
    [Fact]
    public async Task PendingAndCompletedValuesOnlyRefreshTheirOwnWorkflowAsync()
    {
        EditorDocument model = new(new(new BrowserTestRuntime()));
        WorkflowDefinition document = new("workflow", "Workflow", [new("node", "control.delay", JsonSerializer.SerializeToElement(new { milliseconds = 1 }))], []);
        await model.LoadAsync(new(document, "revision", DateTimeOffset.UtcNow, EActivationPolicy.Auto)).ConfigureAwait(true);
        EditorValueStates values = new();
        NodeValueObservation pending = new(Guid.NewGuid(), Guid.NewGuid(), document.Id, "revision", "node", "peer", DateTimeOffset.UtcNow);
        values.Update("cluster", [pending]);
        Assert.Contains("node", values.RunningNodes("cluster", model.Draft));
        Assert.Empty(values.RunningNodes("other-cluster", model.Draft));
        Assert.Equal("null", values.Display("cluster", model.Draft, "node"));
        int version = values.VersionFor("cluster", model.Draft);
        values.Update("cluster", [pending, pending with { WorkflowId = "other", CompletedAt = DateTimeOffset.UtcNow, Outcome = EOutcome.Succeeded }]);
        Assert.Equal(version, values.VersionFor("cluster", model.Draft));
        NodeValueObservation success = pending with { CompletedAt = DateTimeOffset.UtcNow, Outcome = EOutcome.Succeeded };
        values.Update("cluster", [success]);
        Assert.Empty(values.RunningNodes("cluster", model.Draft));
        Assert.NotEqual(version, values.VersionFor("cluster", model.Draft));
        Assert.Equal("true", values.Display("cluster", model.Draft, "node"));
        values.Update("cluster", [pending, success with { CausationId = Guid.NewGuid(), StartedAt = pending.StartedAt.AddSeconds(1) }]);
        Assert.Contains("node", values.RunningNodes("cluster", model.Draft));
        Assert.Equal("true", values.Display("cluster", model.Draft, "node"));
        values.Update("cluster", [success with { Outcome = EOutcome.Failed }]);
        Assert.Equal("false", values.Display("cluster", model.Draft, "node"));
        values.Update("cluster", [success with { Outcome = EOutcome.Unknown }]);
        Assert.Equal("null", values.Display("cluster", model.Draft, "node"));
        values.Update("cluster", [success with { Revision = "old" }]);
        Assert.Empty(values.RunningNodes("cluster", model.Draft));
        Assert.Equal("null", values.Display("cluster", model.Draft, "node"));
        values.Update("cluster", [success]);
        model.ChangeNode("node", node => node with { Settings = JsonSerializer.SerializeToElement(new { milliseconds = 2 }) });
        values.Update("cluster", [pending]);
        Assert.Empty(values.RunningNodes("cluster", model.Draft));
        Assert.Equal("null", values.Display("cluster", model.Draft, "node"));
        version = values.VersionFor("cluster", model.Draft);
        values.Update("cluster", [success with { Outcome = EOutcome.Failed }]);
        Assert.Equal(version, values.VersionFor("cluster", model.Draft));
    }
}
