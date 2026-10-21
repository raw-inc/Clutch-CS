using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Collaboration;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.Operations.Snapshots;
using ReArtWord.Contents.BackgroundService.Contracts.Collaboration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>選択範囲のスナップショットと編集保護の境界を検証します。</summary>
public sealed class SnapshotEditingTests
{
    /// <summary>全体から部分は選べますが、部分から全体・未収録ノードへ拡大できません。</summary>
    [Fact]
    public void SnapshotCoverageNeverExpandsStoredSubset()
    {
        SnapshotListSummary whole = new("snapshot", "flow", "peer", ESnapshotKind.Configuration, DateTimeOffset.UtcNow, "hash", null, "id-hash");
        SnapshotListSummary subset = whole with { NodeIds = ["a"] };
        SnapshotTarget selected = new("flow", "peer", ESnapshotKind.Configuration, ["a"]);
        Assert.True(SnapshotSelectionRules.Covers(whole, selected));
        Assert.True(SnapshotSelectionRules.Covers(subset, selected));
        Assert.False(SnapshotSelectionRules.Covers(subset, selected with { NodeIds = null }));
        Assert.False(SnapshotSelectionRules.Covers(subset, selected with { NodeIds = ["a", "b"] }));
        Assert.False(SnapshotSelectionRules.Covers(whole, selected with { PeerId = "other" }));
        Assert.False(SnapshotSelectionRules.Covers(whole with { ScopeOmitted = true, DetailReason = "snapshot.metadata.size" }, selected));
    }

    /// <summary>保護ノード・接続だけを拒否し、他ノード・ワークフロー名の編集は継続できます。</summary>
    [Fact]
    public void PartialRestoreProtectsOnlyAffectedNodesAndWires()
    {
        WorkflowDefinition before = Definition();
        EditorLocks locks = new();
        locks.Update([new(Guid.NewGuid(), "flow", ["a"])]);
        Assert.True(locks.Allows(before, before with { Name = "outside" }));
        Assert.True(locks.Allows(before, before with { Nodes = [before.Nodes[0], before.Nodes[1] with { Position = new(100, 200) }] }));
        Assert.False(locks.Allows(before, before with { Nodes = [before.Nodes[0] with { Position = new(100, 200) }, before.Nodes[1]] }));
        Assert.False(locks.Allows(before, before with { Wires = [] }));
        locks.Update([new(Guid.NewGuid(), "flow", null)]);
        Assert.False(locks.Allows(before, before with { Name = "outside" }));
        locks.Update([new(Guid.NewGuid(), "flow", null, IsConfigurationParticipant: true)]);
        Assert.True(locks.Allows(before, before with { Name = "outside" }));
    }

    /// <summary>破棄対象以外の値・未加工の入力とdirtyを維持します。</summary>
    [Fact]
    public void DiscardKeepsUnselectedLocalInput()
    {
        WorkflowDefinition saved = Definition();
        WorkflowDefinition local = saved with { Nodes = [saved.Nodes[0] with { Settings = JsonSerializer.SerializeToElement(new { value = 99 }) }, saved.Nodes[1] with { Settings = JsonSerializer.SerializeToElement(new { value = 42 }) }] };
        EditorDraft draft = new(local, saved, "base", saved, "old", "base", true, [], InputDrafts: new Dictionary<string, string> { ["a/settings/value"] = "{", ["b/settings/value"] = "[" });
        DraftView view = new("flow", "base", "new", saved, [], [], DateTimeOffset.UtcNow);
        EditorDraft result = EditorDraftResolution.Apply(draft, view, new(saved, "base", DateTimeOffset.UtcNow, EActivationPolicy.Explicit), ["a"]);
        Assert.Equal(1, result.Document.Nodes[0].Settings.GetProperty("value").GetInt32());
        Assert.Equal(42, result.Document.Nodes[1].Settings.GetProperty("value").GetInt32());
        Assert.False(result.InputDrafts!.ContainsKey("a/settings/value"));
        Assert.Equal("[", result.InputDrafts["b/settings/value"]);
        Assert.True(result.Dirty);
        Assert.Equal("new", result.SessionVersion);
        Assert.Equal(["b"], EditorDirtyScope.Read(result));
    }

    /// <summary>配線差分の両端と未加工の入力からdirty範囲を計算します。</summary>
    [Fact]
    public void DirtyScopeUsesChangesRatherThanCursorSelection()
    {
        WorkflowDefinition saved = Definition();
        EditorDraft draft = new(saved with { Name = "renamed" }, saved, "base", saved, "v", "base", true, []);
        Assert.Empty(EditorDirtyScope.Read(draft)!);
        Assert.Equal(["a", "b"], EditorDirtyScope.Read(draft with { Document = saved with { Wires = [] } }));
        Assert.Equal(["a"], EditorDirtyScope.Read(draft with { InputDrafts = new Dictionary<string, string> { ["a/settings/value"] = "{" } }));
    }

    /// <summary>境界接続を持つ二つのノードです。</summary>
    /// <returns>試験対象の定義です。</returns>
    private static WorkflowDefinition Definition() => new("flow", "base", [new("a", "value", JsonSerializer.SerializeToElement(new { value = 1 })), new("b", "value", JsonSerializer.SerializeToElement(new { value = 2 }))], [new("a", "value", "b", "value")]);
}
