using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Navigation;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>タブの非表示・並替え・未選択状態がWF本文や草稿を破棄しないことを検証します。</summary>
public sealed class WorkflowTabLayoutTests
{
    /// <summary>並替え・非表示を再読込後も維持し、保存一覧から選ぶと元の順序へ戻せます。</summary>
    /// <returns>ブラウザー保存と候補一覧の独立性です。</returns>
    [Fact]
    public async Task HiddenTabsRetainOrderAndReopenWithoutChangingSavedListAsync()
    {
        BrowserStorage storage = new(new BrowserTestRuntime());
        WorkflowListSummary[] items = [Summary("a", "名前A"), Summary("b", "名前B"), Summary("c", "名前C")];
        EditorWorkflowTabState state = new();
        state.Update(items, "a", "名前A", false, false);
        state.Move("a", "c");
        state.Hide("c");
        Assert.Equal<string>(["b", "a"], state.Update(items, "b", "名前B", false, false).Select(tab => tab.Id));
        await storage.WriteAsync("tabs", state.Export()).ConfigureAwait(true);
        EditorWorkflowTabState restored = new();
        restored.Restore((await storage.ReadAsync<EditorWorkflowTabLayout>("tabs").ConfigureAwait(true))!);
        Assert.Equal<string>(["b", "a"], restored.Update(items, string.Empty, string.Empty, false, false).Select(tab => tab.Id));
        restored.Reveal("c");
        Assert.Equal<string>(["b", "c", "a"], restored.Update(items, "c", "名前C", false, false).Select(tab => tab.Id));
        Assert.Equal<string?>(["a", "b", "c"], items.Select(item => item.Id));
        restored.Hide("a");
        restored.Forget("a");
        Assert.DoesNotContain("a", restored.Export().Order);
        Assert.DoesNotContain("a", restored.Export().Hidden);
        Assert.DoesNotContain(restored.All, tab => string.Equals(tab.Id, "a", StringComparison.Ordinal));
    }

    /// <summary>一覧の更新で表示名を追従し、未保存の名前は上書きしません。</summary>
    [Fact]
    public void SavedNamesRefreshWhileUnsavedDraftNamesRemainOwned()
    {
        EditorWorkflowTabState state = new();
        state.Update([Summary("a", "old")], string.Empty, string.Empty, false, false);
        Assert.Equal("new", Assert.Single(state.Update([Summary("a", "new")], string.Empty, string.Empty, false, false)).Name);
        state.Update([], "a", "draft", true, false);
        state.Hide("a");
        Assert.Empty(state.Update([Summary("a", "server")], string.Empty, string.Empty, false, false));
        Assert.Equal("draft", Assert.Single(state.All).Name);
    }

    /// <summary>全タブを閉じて再読込しても新規WFを作らず、未確定入力を元の草稿から復元します。</summary>
    /// <returns>保存領域と接続先を跨ぐ未選択状態です。</returns>
    [Fact]
    public async Task ClosedSelectionSurvivesReloadAndRetainsDraftAsync()
    {
        BrowserStorage storage = new(new BrowserTestRuntime());
        EditorDocument model = new(storage);
        await model.SwitchTargetAsync("cluster", "peer").ConfigureAwait(true);
        model.New();
        string id = model.Draft.Document.Id;
        model.SetInput("node/value", "{unfinished");
        await model.PersistAsync().ConfigureAwait(true);
        model.Close();
        await model.PersistAsync().ConfigureAwait(true);
        Assert.False(model.HasDocument);
        await model.SwitchTargetAsync("other", "peer").ConfigureAwait(true);
        await model.SwitchTargetAsync("cluster", "peer").ConfigureAwait(true);
        Assert.False(model.HasDocument);
        Assert.Single(await EditorDraftStorage.ReadAllAsync(storage, model.StorageOwner, "cluster", "peer").ConfigureAwait(true));
        Assert.True(await model.TryOpenDraftAsync(id).ConfigureAwait(true));
        Assert.True(model.HasDocument);
        Assert.Equal("{unfinished", model.ReadInput("node/value"));
    }

    /// <summary>名前解決だけに必要な保存済み候補です。</summary>
    /// <returns>WF本文を持たない一覧項目です。</returns>
    private static WorkflowListSummary Summary(string id, string name) => new(id, name, "saved", "saved", DateTimeOffset.UtcNow, 0, false, id);
}
