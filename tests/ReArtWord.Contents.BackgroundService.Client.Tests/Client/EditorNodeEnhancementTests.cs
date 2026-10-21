using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>名前・型・診断の表示が、実際の接続と文書の範囲に一致することを検証します。</summary>
public sealed class EditorNodeEnhancementTests
{
    /// <summary>正常な設定評価・非選択を、手動実行と自動履歴のどちらでもエラーにしません。</summary>
    [Theory]
    [InlineData(EOutcome.NotExecuted, "selection.configuration", false)]
    [InlineData(EOutcome.NotExecuted, "value.configuration", false)]
    [InlineData(EOutcome.NotExecuted, "input.inactive", false)]
    [InlineData(EOutcome.NotExecuted, "input.eventPending", false)]
    [InlineData(EOutcome.NotExecuted, "input.unavailable", true)]
    [InlineData(EOutcome.Failed, "input.eventPending", true)]
    [InlineData(EOutcome.Unknown, "input.eventPending", true)]
    [InlineData(EOutcome.NotExecuted, "iteration.notStarted", false)]
    [InlineData(EOutcome.NotExecuted, "iteration.controlled", false)]
    [InlineData(EOutcome.NotExecuted, "input.branchNotSelected", false)]
    [InlineData(EOutcome.Failed, "input.branchNotSelected", true)]
    [InlineData(EOutcome.Unknown, "input.branchNotSelected", true)]
    [InlineData(EOutcome.Failed, "iteration.controlled", true)]
    [InlineData(EOutcome.Unknown, "iteration.controlled", true)]
    [InlineData(EOutcome.NotExecuted, "button.notPressed", false)]
    [InlineData(EOutcome.NotExecuted, "signal.eventBoundary", false)]
    [InlineData(EOutcome.NotExecuted, "configuration.unavailable", true)]
    [InlineData(EOutcome.NotExecuted, "target.unavailable", true)]
    [InlineData(EOutcome.Failed, "selection.configuration", true)]
    [InlineData(EOutcome.Unknown, "selection.configuration", true)]
    [InlineData(EOutcome.Failed, "button.notPressed", true)]
    [InlineData(EOutcome.Unknown, "signal.eventBoundary", true)]
    public void ExpectedNonExecutionDoesNotHighlightNodesOrTabs(EOutcome outcome, string reason, bool problem)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        NodeResult result = NodeResult.Failure(reason, "Node state", outcome);
        ExecutionReport report = new(Guid.NewGuid(), "flow", "revision", EOutcome.Succeeded, now, now, [new("selection", outcome, [new("peer", result)])]);
        EditorDiagnostics manual = new();
        manual.SetRun("cluster", new(report.Id, "peer", "flow", "revision", report.Outcome, false, report));
        Assert.Equal(problem, manual.Issues("cluster", "flow").Any(issue => issue.NodeId is "selection"));
        Assert.Equal(problem, manual.ClusterHasErrors("cluster"));
        EditorDiagnostics automatic = new();
        automatic.SetHistory("cluster", [new("flow", "revision", "selection", "peer", outcome, reason, now)]);
        Assert.Equal(problem, automatic.Issues("cluster", "flow").Any(issue => issue.NodeId is "selection"));
        Assert.Equal(problem, automatic.ClusterHasErrors("cluster"));
    }

    /// <summary>別対象の正常な未実行が同時にあっても、実際の対象失敗は隠しません。</summary>
    [Fact]
    public void ExpectedSkipOnOnePeerDoesNotHideAnotherPeerFailure()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        NodeResult skipped = NodeResult.Failure("selection.configuration", "Already bound", EOutcome.NotExecuted);
        NodeResult failed = NodeResult.Failure("target.unavailable", "Unavailable", EOutcome.NotExecuted);
        ExecutionReport report = new(Guid.NewGuid(), "flow", "revision", EOutcome.Succeeded, now, now,
            [new("selection", EOutcome.NotExecuted, [new("one", skipped), new("two", failed)])]);
        EditorDiagnostics diagnostics = new();
        diagnostics.SetRun("cluster", new(report.Id, "peer", "flow", "revision", report.Outcome, false, report));
        Assert.Single(diagnostics.Issues("cluster", "flow"));
        diagnostics.SetRun("cluster", new(report.Id, "peer", "flow", "revision", EOutcome.Succeeded, false, report with { Nodes = [] }));
        diagnostics.SetHistory("cluster", [new("flow", "revision", "selection", "one", EOutcome.NotExecuted, skipped.ReasonCode, now),
            new("flow", "revision", "selection", "two", EOutcome.NotExecuted, failed.ReasonCode, now)]);
        Assert.Single(diagnostics.Issues("cluster", "flow"));
    }

    /// <summary>自動実行の最新結果が成功になったら解除し、後着の古い失敗では戻しません。</summary>
    [Fact]
    public void RuntimeHistoryClearsRecoveredNodesWithoutMixingRevisions()
    {
        EditorDiagnostics diagnostics = new();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        NodeExecutionObservation failure = new("flow", "revision", "node", "peer", EOutcome.Failed, "failed", now);
        diagnostics.SetHistory("one", [failure]);
        Assert.Single(diagnostics.Issues("one", "flow"));
        NodeExecutionObservation success = failure with { CompletedAt = now.AddSeconds(1), Outcome = EOutcome.Succeeded };
        diagnostics.SetHistory("one", [failure, success]);
        Assert.Empty(diagnostics.Issues("one", "flow"));
        diagnostics.SetHistory("one", [failure]);
        Assert.Empty(diagnostics.Issues("one", "flow"));
        diagnostics.SetHistory("one", [failure with { CompletedAt = now.AddSeconds(2), Outcome = EOutcome.NotExecuted, ReasonCode = "input.missing" }]);
        Assert.Empty(diagnostics.Issues("one", "flow"));
    }

    /// <summary>OSC整数の別名と空名の継承を、元ポートと型を変更せず表示します。</summary>
    [Fact]
    public void CommentNameAndTypeFollowTheConnectedPort()
    {
        NodeDefinition osc = new("osc", "osc.udp.receive", JsonSerializer.SerializeToElement(new { argumentTypes = (string[])["i"] }));
        NodeDefinition comment = new("note", "value.comment", JsonSerializer.SerializeToElement(new { comment = "説明" }), new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["displayName"] = NodeValue.FromObject("年齢") });
        NodeDefinition next = new("next", "value.comment", JsonSerializer.SerializeToElement(new { displayName = "" }));
        WorkflowDefinition document = new("flow", "Flow", [osc, comment, next], [new("osc", "argument1", "note", "value"), new("note", "value", "next", "value")]);
        UiText ui = new(new(new BrowserTestRuntime()));
        Assert.Equal("年齢", GraphPortNames.Resolve(document, ui, "next", "value", true));
        Assert.Equal("integer", new WorkflowTypeInference(document, []).PortType("next", "value", true)!.Type);
        document = document with { Nodes = [osc, comment with { Inputs = null }, next] };
        Assert.Equal(ui.PortName(osc.Type, "argument1", true), GraphPortNames.Resolve(document, ui, "next", "value", true));
        NodeDefinition name = new("name", "value.constant", JsonSerializer.SerializeToElement(new { text = "Age", type = "string" }));
        document = document with { Nodes = [.. document.Nodes, name], Wires = [.. document.Wires, new("name", "value", "note", "displayName")] };
        Assert.Equal("Age", GraphPortNames.Resolve(document, ui, "next", "value", true));
    }

    /// <summary>直接編集専用コメント欄へは通常の配線操作も許可しません。</summary>
    [Fact]
    public void CommentSettingCannotBeWired()
    {
        JsonElement empty = JsonSerializer.SerializeToElement(new { });
        WorkflowDefinition document = new("flow", "Flow", [new("text", "value.constant", JsonSerializer.SerializeToElement(new { text = "memo", type = "string" })), new("note", "value.comment", empty)], []);
        NodeDescriptor descriptor = new("value.comment", "Comment", "Values", "", ENodeKind.Action, EExecutionLocation.Independent, [new("value")], [new("value")], ["success"],
            JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"comment":{"type":"string","x-connectable":false}}}"""));
        Assert.False(GraphConnectionPolicy.CanConnect(document, [descriptor], new("text", "value", "note", "$settings.comment")));
    }

    /// <summary>コードの配線とサイズを保存し、表示の拡縮を実行設定変更と誤認しません。</summary>
    [Fact]
    public void CodeTextResolvesThroughCommentAndSizeIsAppearanceOnly()
    {
        string code = "public static int Run() => 7;";
        NodeDefinition block = new("code", "value.code", JsonSerializer.SerializeToElement(new { code }));
        NodeDefinition note = new("note", "value.comment", JsonSerializer.SerializeToElement(new { }));
        NodeDefinition script = new("run", "script.csharp", JsonSerializer.SerializeToElement(new { code = "obsolete" }));
        WorkflowDefinition document = new("flow", "Flow", [block, note, script], [new("code", "value", "note", "value"), new("note", "value", "run", "$settings.code")]);
        Assert.Equal(code, ScriptSettings.Request(script.Type, new WorkflowSettingValues(document).Resolve(script).Settings).Code);
        WorkflowDefinition resized = document with { Nodes = [block with { Size = new(700, 600) }, note, script] };
        Assert.Empty(WorkflowChangeInspection.ChangedNodes(document, resized));
        Assert.Equal([EWorkflowChangeKind.Appearance], WorkflowChangeInspection.Kinds(document, resized));
        Assert.Equal(new NodeSize(700, 600), ContractJson.Copy(resized).Nodes[0].Size);
    }

    /// <summary>修正・再検証・待受回復でハイライトを解除し、別クラスタへ漏らしません。</summary>
    [Fact]
    public void DiagnosticsFollowCurrentCodeAndClusterAndClearOnRecovery()
    {
        ScriptAnalysisRequest request = new("csharp", "int n = ;", SyntaxOnly: true);
        ScriptAnalysis analysis = new(ContractJson.Hash(request), null, ["Syntax error"]);
        NodeDefinition code = new("code", "value.code", JsonSerializer.SerializeToElement(new { language = "csharp", code = request.Code, analysis }, ContractJson.Options));
        WorkflowDefinition document = new("flow", "Flow", [code], []);
        EditorDraft draft = new(document, document, "revision", document, null, "revision", false, []);
        EditorDiagnostics diagnostics = new();
        diagnostics.UpdateDraft("one", draft);
        Assert.Single(diagnostics.Issues("one", "flow"));
        Assert.True(diagnostics.ClusterHasErrors("one"));
        Assert.False(diagnostics.ClusterHasErrors("two"));
        WorkflowDefinition fixedDocument = document with { Nodes = [code with { Settings = JsonSerializer.SerializeToElement(new { language = "csharp", code = "int n = 1;", analysis }, ContractJson.Options) }] };
        diagnostics.UpdateDraft("one", draft with { Document = fixedDocument });
        Assert.Empty(diagnostics.Issues("one", "flow"));
        diagnostics.SetValidation("one", document, fixedDocument, [new("old", "Stale", "code")]);
        Assert.Empty(diagnostics.Issues("one", "flow"));
        SourceView failedSource = new("flow", "code", new(ESourceState.Failed, "http.endpoint.duplicate"), PeerId: "peer", StartedAt: DateTimeOffset.UtcNow);
        diagnostics.SetSources("one", [failedSource]);
        Assert.True(diagnostics.ClusterHasErrors("one"));
        diagnostics.SetSources("one", [failedSource, failedSource with { Id = Guid.NewGuid(), StartedAt = failedSource.StartedAt!.Value.AddSeconds(1), Status = new(ESourceState.Listening) }]);
        Assert.False(diagnostics.ClusterHasErrors("one"));
    }
}
