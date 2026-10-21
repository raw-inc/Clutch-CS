using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Collaboration;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Collaboration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>C#化した草稿の精度、再接続、比較を検証します。</summary>
public sealed class ClientEditingTests
{
    /// <summary>reload後も元の要求とSessionVersionを変更しません。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task ReloadPreservesPendingIdentityAndRawInputAsync()
    {
        BrowserTestRuntime script = new();
        BrowserStorage storage = new(script);
        EditorDocument model = new(storage);
        await model.InitializeAsync().ConfigureAwait(true);
        WorkflowDefinition definition = Definition();
        await model.LoadAsync(new(definition, ContractJson.Hash(definition), DateTimeOffset.UtcNow, EActivationPolicy.Auto)).ConfigureAwait(true);
        model.Change(document => document with { Name = "offline" });
        DraftChangeRequest pending = new(Guid.NewGuid(), definition.Id, Guid.NewGuid(), ContractJson.Hash(definition), null, definition, model.Draft.Document, new Dictionary<string, EEditResolution>());
        model.Pending(pending);
        model.SetInput("n/settings/value", "{incomplete");
        await model.PersistAsync().ConfigureAwait(true);
        EditorDocument reloaded = new(storage);
        await reloaded.InitializeAsync().ConfigureAwait(true);
        Assert.Equal(pending.OperationId, reloaded.Draft.Pending!.OperationId);
        Assert.Equal("offline", reloaded.Draft.Document.Name);
        Assert.True(reloaded.HasInvalidInputs);
        Assert.Equal("{incomplete", reloaded.ReadInput("n/settings/value"));
        Assert.Equal(9007199254740993L, reloaded.Draft.Document.Nodes[0].Settings.GetProperty("integer").GetInt64());
    }

    /// <summary>送信中追加編集を共有応答に重ね、他者変更をundoしません。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task AcknowledgementRebasesLaterEditsAndUndoAsync()
    {
        EditorDocument model = new(new(new BrowserTestRuntime()));
        WorkflowDefinition definition = Definition();
        await model.LoadAsync(new(definition, ContractJson.Hash(definition), DateTimeOffset.UtcNow, EActivationPolicy.Auto)).ConfigureAwait(true);
        model.Change(document => document with { Name = "sent" });
        WorkflowDefinition sent = model.Draft.Document;
        model.Change(document => document with { Trace = true });
        WorkflowDefinition remote = sent with { Enabled = true };
        model.Accept(new(definition.Id, ContractJson.Hash(definition), ContractJson.Hash(remote), remote, [], [], DateTimeOffset.UtcNow), sent);
        Assert.True(model.Draft.Document.Trace);
        Assert.True(model.Draft.Document.Enabled);
        model.Undo();
        Assert.False(model.Draft.Document.Trace);
        Assert.True(model.Draft.Document.Enabled);
    }

    /// <summary>存在フラグがnullと削除を区別します。</summary>
    [Fact]
    public void ConflictSelectionDistinguishesNullAndDeletion()
    {
        WorkflowDefinition definition = Definition();
        EditConflict missing = new("/nodes/n/settings/integer", null, JsonSerializer.SerializeToElement(1), null, true, true, false);
        Dictionary<string, int> choices = new(StringComparer.Ordinal) { [missing.Path] = 0 };
        WorkflowDefinition deleted = ConflictSelection.Apply(definition, [missing], choices);
        Assert.False(deleted.Nodes[0].Settings.TryGetProperty("integer", out _));
        WorkflowDefinition explicitNull = ConflictSelection.Apply(definition, [missing with { EditedExists = true }], choices);
        Assert.Equal(JsonValueKind.Null, explicitNull.Nodes[0].Settings.GetProperty("integer").ValueKind);
        Assert.Throws<InvalidOperationException>(() => ConflictSelection.Apply(definition, [missing with { EditedExists = null }], choices));
    }

    /// <summary>旧JS草稿の未確認要求も型付き状態へ移行します。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact(Skip = "旧仕様: 移行後も旧本文を保持する仕様は終了。保存確認後の削除と保存失敗時の保持はLegacyDraftMigrationTestsで検証します。")]
    public async Task PrototypeDraftIsMigratedWithoutDeletingOriginalAsync()
    {
        BrowserTestRuntime script = new();
        WorkflowDefinition definition = Definition();
        script.Session["contents.editor.owner"] = "old-ui";
        script.Session["contents.editor.last"] = definition.Id;
        string key = "contents.draft.old-ui." + definition.Id;
        script.Local[key] = JsonSerializer.Serialize(new { document = definition with { Name = "retained" }, @base = definition, baseRevision = (string?)null, dirty = true, collaboration = new { original = definition, baseRevision = (string?)null, sessionVersion = (string?)null, conflicts = Array.Empty<EditConflict>(), pending = (DraftChangeRequest?)null } }, ContractJson.Options);
        EditorDocument model = new(new(script));
        await model.InitializeAsync().ConfigureAwait(true);
        Assert.Equal("retained", model.Draft.Document.Name);
        Assert.True(script.Local.ContainsKey(key));
    }

    /// <summary>同じワークフロー IDでも異なるクラスタの草稿を混ぜません。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task ClusterSelectionKeepsDraftsSeparateAsync()
    {
        BrowserTestRuntime script = new();
        BrowserStorage storage = new(script);
        EditorDocument model = new(storage);
        await model.InitializeAsync().ConfigureAwait(true);
        WorkflowDefinition definition = Definition();
        StoredWorkflow saved = new(definition, ContractJson.Hash(definition), DateTimeOffset.UtcNow, EActivationPolicy.Auto);
        await model.SwitchClusterAsync("cluster-a").ConfigureAwait(true);
        await model.LoadAsync(saved).ConfigureAwait(true);
        model.Change(document => document with { Name = "draft-a" });
        await model.SwitchClusterAsync("cluster-b").ConfigureAwait(true);
        await model.LoadAsync(saved).ConfigureAwait(true);
        Assert.Equal("base", model.Draft.Document.Name);
        model.Change(document => document with { Name = "draft-b" });
        await model.SwitchClusterAsync("cluster-a").ConfigureAwait(true);
        Assert.Equal("draft-a", model.Draft.Document.Name);
        await model.SwitchClusterAsync("cluster-b").ConfigureAwait(true);
        Assert.Equal("draft-b", model.Draft.Document.Name);
    }

    /// <summary>共有による配線順変更でも同じ配線を選択し、削除ノードの未確定入力を残しません。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task SharedWireSelectionAndDeletedInputRemainConsistentAsync()
    {
        EditorDocument model = new(new(new BrowserTestRuntime()));
        WireDefinition selected = new("n", "value", "other", "value");
        WireDefinition added = new("n", "other", "other", "other");
        WorkflowDefinition definition = Definition() with { Wires = [selected] };
        await model.LoadAsync(new(definition, "base", DateTimeOffset.UtcNow, EActivationPolicy.Auto)).ConfigureAwait(true);
        model.SelectedWire = 0;
        WorkflowDefinition remote = definition with { Wires = [added, selected] };
        model.Accept(new(definition.Id, "base", "session", remote, [], [], DateTimeOffset.UtcNow), definition);
        Assert.NotNull(model.SelectedWire);
        Assert.Equal(selected, model.Draft.Document.Wires[model.SelectedWire.Value]);
        model.SetInput("n/settings/value", "{");
        model.SetInput("workflow/traceRawLimit", "invalid");
        model.Change(document => document with { Nodes = [], Wires = [] });
        Assert.Null(model.ReadInput("n/settings/value"));
        Assert.Equal("invalid", model.ReadInput("workflow/traceRawLimit"));
    }

    /// <summary>オフライン補助データは同じ接続先だけから復元します。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task ReferenceCacheKeepsPeerAndClusterBoundaryAsync()
    {
        BrowserStorage storage = new(new BrowserTestRuntime());
        EditorReferenceData saved = new([], []);
        Assert.True(await saved.WriteAsync(storage, "cluster-a", "peer-a").ConfigureAwait(true));
        Assert.NotNull(await EditorReferenceData.ReadAsync(storage, "cluster-a", "peer-a").ConfigureAwait(true));
        Assert.Null(await EditorReferenceData.ReadAsync(storage, "cluster-b", "peer-a").ConfigureAwait(true));
        Assert.Null(await EditorReferenceData.ReadAsync(storage, "cluster-a", "peer-b").ConfigureAwait(true));
    }

    /// <summary>同じ内容を返す選択操作ではdirty・undo・共同編集通知を増やしません。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task IdenticalEditDoesNotDirtyOrNotifyAsync()
    {
        EditorDocument model = new(new(new BrowserTestRuntime()));
        WorkflowDefinition definition = Definition();
        await model.LoadAsync(new(definition, "base", DateTimeOffset.UtcNow, EActivationPolicy.Auto)).ConfigureAwait(true);
        int changes = 0;
        model.Changed += () => changes++;
        model.Change(document => document with { Name = document.Name });
        model.ChangeNode("n", node => node with { Position = node.Position });
        Assert.False(model.Draft.Dirty);
        Assert.False(model.CanUndo);
        Assert.Equal(0, changes);
    }

    /// <summary>大きい整数を含む汎用定義です。</summary>
    /// <returns>独立した定義です。</returns>
    private static WorkflowDefinition Definition()
    {
        using JsonDocument settings = JsonDocument.Parse("{\"integer\":9007199254740993}");
        return new("flow", "base", [new("n", "value", settings.RootElement.Clone())], []);
    }
}
