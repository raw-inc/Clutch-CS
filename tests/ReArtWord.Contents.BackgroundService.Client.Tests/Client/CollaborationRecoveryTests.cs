using System.Net;
using System.Text;
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

/// <summary>表示順の違いで版検証が壊れる事象と、保持済み不正比較元からの復帰を検証します。</summary>
public sealed class CollaborationRecoveryTests
{
    /// <summary>順序の異なる応答後も原文と版が一致し、追加編集と保存確認が成立します。</summary>
    /// <returns>既存草稿の修復と、新しい草稿の正常な継続です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DifferentDisplayOrderDoesNotBlockFurtherChangesAsync(bool retainedLegacyDraft)
    {
        NodeDefinition first = new("a", "control.pass", JsonSerializer.SerializeToElement(new { }));
        NodeDefinition second = first with { Id = "b" };
        WorkflowDefinition initial = new("flow", "original", [first, second], []);
        WorkflowDefinition remote = initial with { Nodes = [second, first] };
        DraftView view = new(initial.Id, ContractJson.Hash(initial), ContractJson.Hash(remote), remote, [], [], DateTimeOffset.UtcNow);
        List<DraftChangeRequest> changes = [];
        using ClientHttpHandler handler = new(async (request, token) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            string draftSuffix = "/draft", changesSuffix = "/changes";
            if (path.EndsWith(draftSuffix, StringComparison.Ordinal))
            {
                return Reply(view);
            }
            if (path.EndsWith(changesSuffix, StringComparison.Ordinal))
            {
                DraftChangeRequest change = JsonSerializer.Deserialize<DraftChangeRequest>(await request.Content!.ReadAsStringAsync(token).ConfigureAwait(false), ContractJson.Options)!;
                changes.Add(change);
                Assert.Equal(ContractJson.Hash(change.Original), change.BaseSessionVersion);
                view = view with { Definition = change.Edited, SessionVersion = ContractJson.Hash(change.Edited) };
                return Reply(view);
            }
            return Reply(Array.Empty<PresenceView>());
        });
        using HttpClient client = new(handler) { BaseAddress = new Uri("http://localhost/") };
        EditorDocument model = new(new(new BrowserTestRuntime()));
        await model.LoadAsync(new(initial, ContractJson.Hash(initial), DateTimeOffset.UtcNow, EActivationPolicy.Auto)).ConfigureAwait(true);
        EditorCollaboration collaboration = new(model, new(client));
        if (retainedLegacyDraft)
        {
            model.SetBaseline(view with { Definition = initial });
            model.Change(document => document with { Name = "local edit" });
        }
        else
        {
            await collaboration.SynchronizeAsync(true, "host").ConfigureAwait(true);
            Assert.Equal(view.SessionVersion, ContractJson.Hash(model.Draft.Original));
            Assert.False(collaboration.Pending);
            model.Change(document => document with { Name = "local edit" });
        }
        await collaboration.SynchronizeAsync(true, "host").ConfigureAwait(true);
        Assert.Single(changes);
        Assert.Equal("local edit", model.Draft.Document.Name);
        Assert.Null(model.Draft.Pending);
        Assert.Empty(collaboration.Validation);
        collaboration.EnsureConfirmed();
        await collaboration.SynchronizeAsync(true, "host").ConfigureAwait(true);
        Assert.Single(changes);
    }

    /// <summary>一時的な取消でactiveを残さず、次の照会で保存可能状態へ戻ります。</summary>
    /// <returns>要求の再開です。</returns>
    [Fact]
    public async Task CancelledReadCanBeConfirmedOnTheNextAttemptAsync()
    {
        WorkflowDefinition definition = new("flow", "name", [], []);
        bool fail = true;
        using ClientHttpHandler handler = new((request, _) =>
        {
            if (fail)
            {
                fail = false;
                throw new OperationCanceledException();
            }
            bool draft = request.RequestUri!.AbsolutePath.EndsWith("/draft", StringComparison.Ordinal);
            return Task.FromResult(draft ? Reply(new DraftView(definition.Id, "saved", ContractJson.Hash(definition), definition, [], [], DateTimeOffset.UtcNow))
                                         : Reply(Array.Empty<PresenceView>()));
        });
        using HttpClient client = new(handler) { BaseAddress = new Uri("http://localhost/") };
        EditorDocument model = new(new(new BrowserTestRuntime()));
        await model.LoadAsync(new(definition, "saved", DateTimeOffset.UtcNow, EActivationPolicy.Auto)).ConfigureAwait(true);
        EditorCollaboration collaboration = new(model, new(client));
        await collaboration.SynchronizeAsync(true, "host").ConfigureAwait(true);
        Assert.False(collaboration.Active);
        Assert.Throws<InvalidOperationException>(collaboration.EnsureConfirmed);
        await collaboration.SynchronizeAsync(true, "host").ConfigureAwait(true);
        collaboration.EnsureConfirmed();
    }

    /// <summary>別画面で解消した共有競合を、受信側の古い表示から再追加しません。</summary>
    /// <returns>本文が同じまま競合だけ解消した場合の確認です。</returns>
    [Fact]
    public async Task SharedResolutionRemovesOnlySharedConflictsAsync()
    {
        WorkflowDefinition definition = new("flow", "name", [], []);
        EditorDocument model = new(new(new BrowserTestRuntime()));
        await model.LoadAsync(new(definition, "saved", DateTimeOffset.UtcNow, EActivationPolicy.Auto)).ConfigureAwait(true);
        EditConflict conflict = new("/name", JsonSerializer.SerializeToElement("base"), JsonSerializer.SerializeToElement("name"), JsonSerializer.SerializeToElement("other"));
        DraftView view = new(definition.Id, "saved", ContractJson.Hash(definition), definition, [conflict], [], DateTimeOffset.UtcNow);
        model.Accept(view, definition);
        Assert.Single(model.Draft.Conflicts);
        model.Accept(view with { Conflicts = [] }, definition);
        Assert.Empty(model.Draft.Conflicts);
    }

    /// <summary>強制保存の後も元の競合選択を共有し、無操作の再照会で復活させません。</summary>
    /// <returns>保存中の追加編集は次の差分として維持します。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForcedSaveReconcilesSharedConflictsBeforeIdleReadsAsync(bool additionalEdit)
    {
        WorkflowDefinition definition = new("flow", "shared", [], []);
        EditConflict conflict = new("/name", JsonSerializer.SerializeToElement("base"), JsonSerializer.SerializeToElement("shared"), JsonSerializer.SerializeToElement("local"));
        DraftView view = new(definition.Id, "old", ContractJson.Hash(definition), definition, [conflict], [], DateTimeOffset.UtcNow);
        EditorDocument model = new(new(new BrowserTestRuntime()));
        await model.LoadAsync(new(definition, "old", DateTimeOffset.UtcNow, EActivationPolicy.Auto)).ConfigureAwait(true);
        model.Accept(view, definition);
        WorkflowDefinition saved = definition with { Name = "local" };
        model.Change(_ => saved);
        if (additionalEdit)
        {
            model.Change(document => document with { Enabled = true });
        }
        model.Saved("saved", saved, force: true);
        int changes = 0;
        using ClientHttpHandler handler = new(async (request, token) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            bool change = path.EndsWith("/changes", StringComparison.Ordinal), draft = path.EndsWith("/draft", StringComparison.Ordinal);
            if (change)
            {
                DraftChangeRequest input = JsonSerializer.Deserialize<DraftChangeRequest>(await request.Content!.ReadAsStringAsync(token).ConfigureAwait(false), ContractJson.Options)!;
                if (changes == 0)
                {
                    Assert.Equal(EEditResolution.UseEdited, input.Resolutions["/name"]);
                }
                changes++;
                view = view with { Definition = input.Edited, SessionVersion = ContractJson.Hash(input.Edited), Conflicts = [] };
                return Reply(view);
            }
            if (draft)
            {
                return Reply(view);
            }
            return Reply(Array.Empty<PresenceView>());
        });
        using HttpClient client = new(handler) { BaseAddress = new Uri("http://localhost/") };
        EditorCollaboration collaboration = new(model, new(client));
        for (int pass = 0; pass < 4; pass++)
        {
            await collaboration.SynchronizeAsync(true, "host").ConfigureAwait(true);
        }
        collaboration.EnsureConfirmed();
        Assert.Empty(model.Draft.Conflicts);
        Assert.Equal("local", model.Draft.Document.Name);
        Assert.Equal(additionalEdit, model.Draft.Document.Enabled);
        Assert.Equal(additionalEdit, model.Draft.Dirty);
        int expectedChanges = 1;
        if (additionalEdit)
        {
            expectedChanges = 2;
        }
        Assert.Equal(expectedChanges, changes);
    }

    /// <summary>保持範囲外の全文比較でも、保持と候補採用を別々に解消できます。</summary>
    [Fact]
    public void WholeDraftConflictSelectionPreservesTheChosenDocument()
    {
        WorkflowDefinition current = new("flow", "current", [], []);
        WorkflowDefinition edited = current with { Name = "candidate", Enabled = true };
        EditConflict conflict = new(string.Empty, null, JsonSerializer.SerializeToElement(current, ContractJson.Options),
            JsonSerializer.SerializeToElement(edited, ContractJson.Options), false, true, true);
        WorkflowDefinition chosen = ConflictSelection.Apply(current, [conflict], new Dictionary<string, int> { [string.Empty] = 0 });
        Assert.Equal("candidate", chosen.Name);
        Assert.True(chosen.Enabled);
        WorkflowDefinition retained = ConflictSelection.Apply(current, [conflict], new Dictionary<string, int> { [string.Empty] = -1 });
        Assert.Equal("current", retained.Name);
        Assert.False(retained.Enabled);
    }

    /// <summary>製品と同じJSON契約を返します。</summary>
    /// <returns>呼出元が破棄する応答です。</returns>
    private static HttpResponseMessage Reply<T>(T value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value, ContractJson.Options), Encoding.UTF8, "application/json") };
}
