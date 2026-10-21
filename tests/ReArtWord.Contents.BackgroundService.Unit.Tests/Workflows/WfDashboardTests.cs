using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow.Dashboards;
using ReArtWord.Contents.BackgroundService.CoreService.Management.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>参照定義の永続化と非実行性、型を固定したValue編集を確認します。</summary>
public sealed class WfDashboardTests
{
    /// <summary>配置を保存しても実行ノードを生成せず、通常WFへの変換を拒否します。</summary>
    /// <returns>保存、復元、種類の不変性です。</returns>
    [Fact]
    public async Task SavesReferenceLayoutWithoutExecutableNodesAndRejectsKindConversionAsync()
    {
        WfDashboardFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        await fixture.StartAsync().ConfigureAwait(true);
        WorkflowDefinition document = Dashboard();
        CompilationResult compiled = fixture.Compiler.Compile(document);
        Assert.Empty(compiled.Issues);
        Assert.Empty(compiled.Workflow!.OrderedNodes);
        WorkflowFixture engine = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable engineLifetime = engine.ConfigureAwait(true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.Engine.ExecuteAsync(compiled.Workflow)).ConfigureAwait(true);
        Contracts.Configuration.Workflows.WorkflowSaveResult saved = await fixture.Store.SaveAsync(new(Guid.NewGuid(), document, null)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Saved.Outcome);
        Assert.Equal(ContractJson.Hash(document), ContractJson.Hash((await fixture.Store.GetAsync(document.Id).ConfigureAwait(true))!.Definition));
        WorkflowDefinition converted = document with { IsDashboard = false, Nodes = [] };
        Assert.Contains((await fixture.Store.SaveAsync(new(Guid.NewGuid(), converted, saved.Revision)).ConfigureAwait(true)).Validation, issue => issue.Code is "workflow.kind");
        Assert.True((await fixture.Store.GetAsync(document.Id).ConfigureAwait(true))!.Definition.IsDashboard);
        Assert.Contains(WorkflowEditorMerge.Merge(document, document, new(Guid.NewGuid(), saved.Revision!, converted, new Dictionary<string, EEditResolution>())).Validation,
            issue => issue.Code is "workflow.kind");
        WorkflowDefinition normal = new("normal", "Normal", [], []);
        Contracts.Configuration.Workflows.WorkflowSaveResult normalSaved = await fixture.Store.SaveAsync(new(Guid.NewGuid(), normal, null)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, normalSaved.Saved.Outcome);
        Assert.Contains((await fixture.Store.SaveAsync(new(Guid.NewGuid(), normal with { IsDashboard = true }, normalSaved.Revision)).ConfigureAwait(true)).Validation,
            issue => issue.Code is "workflow.kind");
    }

    /// <summary>実行設定・配線・非対応ノードを参照カードへ混入できません。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RejectsExecutableOrMalformedDashboardDefinitions(int mutation)
    {
        WorkflowDefinition document = Dashboard();
        document = mutation switch
        {
            0 => document with { Enabled = true },
            1 => document with { IsRecipe = true },
            2 => document with { Wires = [new("card", "value", "card", "value")] },
            _ => document with { Nodes = [document.Nodes[0] with { Type = "script.csharp" }] },
        };
        WorkflowCompiler compiler = new(new([]));
        Assert.Null(compiler.Compile(document).Workflow);
    }

    /// <summary>入力文字列だけを変更し、型・配線・論理名を維持します。</summary>
    /// <returns>準備に書込みがないこと、保存の競合と型変更拒否の確認です。</returns>
    [Theory]
    [InlineData("integer", "12", "34", "hello")]
    [InlineData("auto", "12", "34", "hello")]
    [InlineData("string", "start", "123", null)]
    [InlineData("auto", "start", "123", null)]
    [InlineData("number", "12", "34.5", "hello")]
    [InlineData("decimal", "12", "34.5", "hello")]
    [InlineData("byte", "12", "255", "256")]
    [InlineData("char", "A", "B", "CC")]
    public async Task ValueEditingPreservesTypeAndRejectsStaleRevisionAsync(string type, string text, string next, string? invalid)
    {
        WfDashboardFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        await fixture.StartAsync().ConfigureAwait(true);
        NodeDefinition node = new("value", "value.constant", JsonSerializer.SerializeToElement(new { type, text }), LogicalName: "Amount");
        WorkflowDefinition source = new("source", "Source", [node, new("pass", "control.pass", JsonSerializer.SerializeToElement(new { }))], [new("value", "value", "pass", "value")]);
        Contracts.Configuration.Workflows.WorkflowSaveResult saved = await fixture.Store.SaveAsync(new(Guid.NewGuid(), source, null)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Saved.Outcome);
        WfDashboardValues values = new(fixture.Store, fixture.Registry);
        WfDashboardReference reference = new(source.Id, node.Id, node.Type);
        WfDashboardValuePreparation prepared = await values.PrepareAsync(new(Guid.NewGuid(), reference, saved.Revision!, next)).ConfigureAwait(true);
        Assert.Empty(prepared.Issues);
        Assert.NotNull(prepared.Request);
        Assert.Equal(saved.Revision, (await fixture.Store.GetAsync(source.Id).ConfigureAwait(true))!.Revision);
        Assert.Equal(source.Wires, prepared.Request.Definition.Wires);
        Assert.Equal(node.LogicalName, prepared.Request.Definition.Nodes[0].LogicalName);
        Assert.Equal(type, prepared.Request.Definition.Nodes[0].Settings.GetProperty("type").GetString());
        Assert.Equal(values.Read(source, node)!.Type.Type, values.Read(prepared.Request.Definition, prepared.Request.Definition.Nodes[0])!.Type.Type);
        if (invalid is not null)
        {
            Assert.Null((await values.PrepareAsync(new(Guid.NewGuid(), reference, saved.Revision!, invalid)).ConfigureAwait(true)).Request);
        }
        Contracts.Configuration.Workflows.WorkflowSaveResult changed = await fixture.Store.SaveAsync(prepared.Request).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, changed.Saved.Outcome);
        Assert.Null((await values.PrepareAsync(new(Guid.NewGuid(), reference, saved.Revision!, next)).ConfigureAwait(true)).Request);
    }

    /// <summary>論理名が重複しても物理参照は混ざらず、改名では参照が切れません。</summary>
    [Fact]
    public void PhysicalReferenceSurvivesRenameAndRejectsTypeReplacement()
    {
        NodeDefinition first = new("first", "control.button", JsonSerializer.SerializeToElement(new { }), LogicalName: "Cue");
        WorkflowDefinition source = new("source", "Source", [first, first with { Id = "second" }], []);
        WfDashboardReference reference = new("source", "first", first.Type);
        Assert.Equal("first", reference.Find(source)!.Id);
        source = source with { Nodes = [first with { LogicalName = "Renamed" }] };
        Assert.Equal("Renamed", reference.Find(source)!.LogicalName);
        Assert.Null(reference.Find(source with { Nodes = [first with { Type = "control.toggleButton" }] }));
        Assert.Null(reference.Find(source with { Id = "different" }));
    }

    /// <summary>外部接続で指定された型を固定し、値そのものを接続した設定は編集しません。</summary>
    /// <returns>型の幅と配線を維持した検証です。</returns>
    [Fact]
    public async Task HonorsConnectedTypeAndRefusesConnectedTextAsync()
    {
        WfDashboardFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        await fixture.StartAsync().ConfigureAwait(true);
        NodeDefinition type = new("type", "value.type", JsonSerializer.SerializeToElement(new { type = "byte" }));
        NodeDefinition node = new("value", "value.constant", JsonSerializer.SerializeToElement(new { text = "12" }));
        WorkflowDefinition source = new("typed", "Typed", [type, node], [new("type", "value", "value", "type")]);
        Contracts.Configuration.Workflows.WorkflowSaveResult saved = await fixture.Store.SaveAsync(new(Guid.NewGuid(), source, null)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Saved.Outcome);
        WfDashboardValues values = new(fixture.Store, fixture.Registry);
        Assert.Equal("byte", values.Read(source, node)!.Type.Type);
        WfDashboardReference reference = new(source.Id, node.Id, node.Type);
        Assert.NotNull((await values.PrepareAsync(new(Guid.NewGuid(), reference, saved.Revision!, "255")).ConfigureAwait(true)).Request);
        Assert.Null((await values.PrepareAsync(new(Guid.NewGuid(), reference, saved.Revision!, "256")).ConfigureAwait(true)).Request);
        WorkflowDefinition wiredText = source with { Wires = [.. source.Wires, new("type", "value", "value", "$settings.text")] };
        Assert.Null(values.Read(wiredText, node));
    }

    /// <summary>他WFのボタンを参照する最小の保存レイアウトです。</summary>
    /// <returns>配線も実行設定も持たない定義です。</returns>
    private static WorkflowDefinition Dashboard() => new("dashboard", "Controls", [new("card", WfDashboardReference.CardType,
        new WfDashboardReference("source", "button", "control.button").ToSettings(), Position: new(0, 0)),
        new("comment", WfDashboardLayout.CommentType, WfDashboardLayout.CommentSettings("Cue notes\n日本語のメモ"), Position: new(1, 0)),
        new("break", WfDashboardLayout.BreakType, WfDashboardLayout.BreakSettings(), Position: new(2, 0))], []) { IsDashboard = true };
}
