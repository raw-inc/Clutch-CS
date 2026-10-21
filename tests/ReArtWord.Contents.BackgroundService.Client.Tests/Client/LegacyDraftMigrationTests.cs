using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>旧草稿を保存確認前に消さず、変更内容と保存所有範囲を維持して移します。</summary>
public sealed class LegacyDraftMigrationTests
{
    private static readonly string[] legacyAddress = ["cluster", "wf"];

    /// <summary>新画面の保存が失敗した場合は旧草稿を残し、成功したときだけ除去します。</summary>
    /// <returns>クラスタ別の旧草稿からPeer別草稿への移行です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClusterDraftIsRemovedOnlyAfterVerifiedSaveAsync(bool fail)
    {
        BrowserTestRuntime browser = new() { FailWrites = fail };
        BrowserStorage storage = new(browser);
        Guid owner = Guid.NewGuid();
        WorkflowDefinition definition = new("wf", "memo", [], []);
        EditorDraft draft = new(definition, definition, null, definition, null, null, true, []);
        string key = "contents.blazor.draft." + owner.ToString("D") + "." + JsonSerializer.Serialize(legacyAddress);
        browser.Local[key] = JsonSerializer.Serialize(draft, ContractJson.Options);
        Assert.Equal(ContractJson.Hash(draft), ContractJson.Hash(await EditorDraftStorage.ReadAsync(storage, owner, "cluster", "peer", "wf")));
        Assert.Equal(fail, browser.Local.ContainsKey(key));
        browser.FailWrites = false;
        Assert.Equal(ContractJson.Hash(draft), ContractJson.Hash(await EditorDraftStorage.ReadAsync(storage, owner, "cluster", "peer", "wf")));
        Assert.False(browser.Local.ContainsKey(key));
    }

    /// <summary>旧JavaScript画面の未保存内容と比較元を、新草稿へ引き継いでから除去します。</summary>
    /// <returns>旧画面の協調編集が未開始でも内容を保持する移行です。</returns>
    [Fact]
    public async Task OriginalEditorDraftKeepsChangesAndBaselineAsync()
    {
        BrowserTestRuntime browser = new();
        BrowserStorage storage = new(browser);
        WorkflowDefinition baseline = new("wf", "original", [], []);
        WorkflowDefinition edited = baseline with { Name = "unsaved" };
        browser.Session["contents.editor.owner"] = "old";
        browser.Session["contents.editor.last"] = "wf";
        browser.Local["contents.draft.old.wf"] = JsonSerializer.Serialize(new { dirty = true, document = edited, @base = baseline, baseRevision = "revision" });
        Guid owner = Guid.NewGuid();
        EditorDraft draft = (await LegacyDraftReader.MigrateAsync(storage, owner, "cluster", "peer"))!;
        Assert.Equal("unsaved", draft.Document.Name);
        Assert.Equal("original", draft.Base.Name);
        Assert.True(draft.Dirty);
        Assert.False(browser.Local.ContainsKey("contents.draft.old.wf"));
        Assert.Equal(ContractJson.Hash(draft), ContractJson.Hash(await EditorDraftStorage.ReadAsync(storage, owner, "cluster", "peer", "wf")));
    }

    /// <summary>初回保存だけ失敗しても、所有者の確保を移行完了と誤認せず再起動時に再試行します。</summary>
    /// <returns>保存失敗を跨ぐ旧草稿の保持と再取得です。</returns>
    [Fact]
    public async Task InitializationRetriesUncommittedLegacyDraftAsync()
    {
        BrowserTestRuntime browser = new() { RetainDraftOwner = true, RejectWrite = (key, _) => key.StartsWith("contents.blazor.draft.", StringComparison.Ordinal) };
        BrowserStorage storage = new(browser);
        WorkflowDefinition definition = new("wf", "unsaved", [], []);
        browser.Session["contents.editor.owner"] = "old";
        browser.Session["contents.editor.last"] = "wf";
        browser.Local["contents.draft.old.wf"] = JsonSerializer.Serialize(new { dirty = true, document = definition, @base = definition, baseRevision = "revision" });
        (Guid Owner, string? Cluster, string? Peer, EditorDraft? Draft) first = await EditorDraftStorage.InitializeAsync(storage, Guid.NewGuid());
        Assert.True(first.Draft?.Dirty);
        Assert.True(browser.Local.ContainsKey("contents.draft.old.wf"));
        browser.RejectWrite = null;
        (Guid Owner, string? Cluster, string? Peer, EditorDraft? Draft) second = await EditorDraftStorage.InitializeAsync(storage, Guid.NewGuid());
        Assert.Equal("unsaved", second.Draft!.Document.Name);
        Assert.False(browser.Local.ContainsKey("contents.draft.old.wf"));
    }
}
