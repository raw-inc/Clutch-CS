using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Navigation;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>接続先の閲覧では草稿を増やさず、別画面の表示情報だけでは開けるタブと扱いません。</summary>
public sealed class WorkflowTabRecoveryTests
{
    /// <summary>保存一覧が正常でも、草稿領域の読取り失敗時はタブを不在と判断しません。</summary>
    /// <returns>実コンポーネントの表示と、読取り完了を条件とする整理です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ComponentRequiresReadableDraftStorageBeforeCleanupAsync(bool unreadable)
    {
        BrowserTestRuntime browser = new() { RejectRead = key => unreadable && key.StartsWith("contents.blazor.editor.index.", StringComparison.Ordinal) };
        BrowserStorage storage = new(browser);
        EditorWorkflowTabLayout layout = new(["orphan"], [], [new("orphan", "New workflow", false, false)]);
        await storage.WriteAsync("contents.editor.tabs.v1.[null,null]", layout).ConfigureAwait(true);
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(storage);
        services.AddSingleton(new EditorDocument(storage));
        services.AddSingleton(new EditorDiagnostics());
        services.AddSingleton(new UiText(storage));
        using ServiceProvider provider = services.BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = renderer.ConfigureAwait(true);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            ParameterView parameters = ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(EditorWorkflowTabs.ListConfirmed)] = true });
            string html = (await renderer.RenderComponentAsync<EditorWorkflowTabs>(parameters).ConfigureAwait(true)).ToHtmlString();
            Assert.Equal(unreadable, html.Contains("data-workflow-id=\"orphan\"", StringComparison.Ordinal));
        }).ConfigureAwait(true);
    }

    /// <summary>初期化・接続先の往復では本文を作らず、明示新規だけを保持します。</summary>
    /// <returns>新規作成の境界と接続先ごとの復元結果です。</returns>
    [Fact]
    public async Task OpeningConnectionsDoesNotCreateWorkflowsAsync()
    {
        BrowserTestRuntime browser = new() { RetainDraftOwner = true };
        BrowserStorage storage = new(browser);
        EditorDocument model = new(storage);
        Assert.False(model.HasDocument);
        await model.InitializeAsync().ConfigureAwait(true);
        for (int index = 0; index < 3; index++)
        {
            await model.SwitchTargetAsync("one", "peer").ConfigureAwait(true);
            Assert.False(model.HasDocument);
            await model.SwitchTargetAsync("two", "peer").ConfigureAwait(true);
            Assert.False(model.HasDocument);
        }
        Assert.Empty(browser.Local);
        model.New();
        string id = model.Draft.Document.Id;
        await model.PersistAsync().ConfigureAwait(true);
        await model.SwitchTargetAsync("one", "peer").ConfigureAwait(true);
        Assert.False(model.HasDocument);
        await model.SwitchTargetAsync("two", "peer").ConfigureAwait(true);
        Assert.Equal(id, model.Draft.Document.Id);
        Assert.Single(await EditorDraftStorage.ReadAllAsync(storage, model.StorageOwner, "two", "peer").ConfigureAwait(true));
    }

    /// <summary>本文がない前回選択を、別IDのNew workflowへ置き換えません。</summary>
    /// <returns>欠けた草稿からの未選択復元です。</returns>
    [Fact]
    public async Task MissingRetainedDraftLeavesTheSelectionEmptyAsync()
    {
        BrowserStorage storage = new(new BrowserTestRuntime());
        string[] target = ["cluster", "peer"];
        string key = "contents.blazor.editor.last." + JsonSerializer.Serialize(target);
        await storage.WriteAsync(key, "missing", true).ConfigureAwait(true);
        EditorDocument model = new(storage);
        await model.SwitchTargetAsync("cluster", "peer").ConfigureAwait(true);
        Assert.False(model.HasDocument);
        Assert.Empty(await EditorDraftStorage.ReadAllAsync(storage, model.StorageOwner, "cluster", "peer").ConfigureAwait(true));
    }

    /// <summary>別画面にだけ本文があるタブを整理し、元所有者の草稿と同名の保存済みWFは保持します。</summary>
    /// <returns>表示情報と本文の独立性です。</returns>
    [Fact]
    public async Task ForeignTabsAreRemovedWithoutDeletingTheirDraftsAsync()
    {
        BrowserTestRuntime browser = new();
        BrowserStorage storage = new(browser);
        EditorDocument first = new(storage);
        await first.SwitchTargetAsync("cluster", "peer").ConfigureAwait(true);
        first.New();
        await first.PersistAsync().ConfigureAwait(true);
        string orphan = first.Draft.Document.Id;
        EditorWorkflowTabState original = new();
        WorkflowListSummary[] saved = [Summary("saved", "New workflow")];
        original.Update(saved, orphan, "New workflow", false, false);
        original.Hide(orphan);
        EditorWorkflowTabLayout layout = original.Export();
        browser.Session.Clear();
        EditorDocument second = new(storage);
        await second.SwitchTargetAsync("cluster", "peer").ConfigureAwait(true);
        Assert.NotEqual(first.StorageOwner, second.StorageOwner);
        Assert.False(await second.TryOpenDraftAsync(orphan).ConfigureAwait(true));
        EditorWorkflowTabState restored = new();
        restored.Restore(layout);
        restored.Recover(await EditorDraftStorage.ReadAllAsync(storage, second.StorageOwner, "cluster", "peer").ConfigureAwait(true));
        Assert.True(restored.Prune(saved, string.Empty, true));
        Assert.Equal("saved", Assert.Single(restored.Update(saved, string.Empty, string.Empty, false, false)).Id);
        Assert.DoesNotContain(orphan, restored.Export().Order);
        Assert.DoesNotContain(orphan, restored.Export().Hidden);
        Assert.NotNull(await EditorDraftStorage.ReadAsync(storage, first.StorageOwner, "cluster", "peer", orphan).ConfigureAwait(true));
        Assert.False(restored.Prune(saved, string.Empty, true));
    }

    /// <summary>自分の未保存・非表示草稿と、一覧外の現在表示を整理対象にしません。</summary>
    [Fact]
    public void RecoverableDraftsAndCurrentSelectionArePreserved()
    {
        EditorDraft draft = EditorDraftNavigation.Create() with { Dirty = true, InputDrafts = new Dictionary<string, string> { ["node/input"] = "unfinished" } };
        EditorWorkflowTabState state = new();
        state.Restore(new(["current", draft.Document.Id, "missing"], [draft.Document.Id],
            [new("current", "Current", false, false), new(draft.Document.Id, "New workflow", false, false), new("missing", "New workflow", false, false)]));
        state.Recover([draft]);
        Assert.True(state.Prune([], "current", true));
        Assert.Equal("current", Assert.Single(state.Update([], "current", "Current", false, false)).Id);
        Assert.True(state.All.Single(tab => string.Equals(tab.Id, draft.Document.Id, StringComparison.Ordinal)).Dirty);
        Assert.Contains(draft.Document.Id, state.Export().Hidden);
        state.Reveal(draft.Document.Id);
        Assert.Equal(2, state.Update([], "current", "Current", false, false).Count);
    }

    /// <summary>通信未確認・ページ途中・識別子省略の一覧から、本文の不在を推測しません。</summary>
    [Fact]
    public void IncompleteListsCannotRemoveTabs()
    {
        EditorWorkflowTabState state = new();
        state.Restore(new(["unknown"], [], [new("unknown", "New workflow", false, false)]));
        Assert.False(state.Prune([], string.Empty, false));
        Assert.False(state.Prune([Summary("omitted", "Long ID") with { Id = null }], string.Empty, true));
        Assert.Single(state.All);
    }

    /// <summary>別の接続先を復元するとき、前の保持情報と所有済み判定を混ぜません。</summary>
    [Fact]
    public void RestoringAnotherScopeDoesNotRetainPreviousTabs()
    {
        EditorWorkflowTabState state = new();
        state.Update([], "previous", "Previous", true, false);
        state.Restore(new(["current"], [], [new("current", "Current", true, false)]));
        Assert.Equal("current", Assert.Single(state.All).Id);
        WorkflowListSummary[] saved = [Summary("current", "Saved name")];
        EditorWorkflowTab tab = Assert.Single(state.Update(saved, string.Empty, string.Empty, false, false));
        Assert.False(tab.Dirty);
        Assert.Equal("Saved name", tab.Name);
    }

    /// <summary>同名でも識別子で照合する保存済み一覧です。</summary>
    /// <returns>本文を含まない候補です。</returns>
    private static WorkflowListSummary Summary(string id, string name) => new(id, name, "revision", "revision", DateTimeOffset.UtcNow, 0, false, id);
}
