using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Contracts.Collaboration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>同じ所有草稿の再保存を軽量化しても、編集・未確定入力・保存障害を失いません。</summary>
public sealed class EditorDraftPersistenceTests(ITestOutputHelper output)
{
    /// <summary>変更のない大きな草稿を再直列化せず、保存先消失後も現在値を復元します。</summary>
    /// <returns>反復保存の割当量と、再読取りした元の本文です。</returns>
    [Fact]
    public async Task RepeatedDraftAvoidsLargeAllocationsAndStillChecksStorageAsync()
    {
        BrowserTestRuntime script = new();
        BrowserStorage storage = new(script);
        Guid owner = Guid.NewGuid();
        WorkflowDefinition definition = new("large", "original", [new("n", "value.constant", JsonSerializer.SerializeToElement(new { text = new string('x', 1048576) }))], []);
        string revision = ContractJson.Hash(definition);
        EditorDraft draft = new(definition, definition, revision, definition, revision, revision, false, []);
        Assert.Null(await EditorDraftStorage.WriteAsync(storage, owner, "cluster", "peer", draft).ConfigureAwait(true));
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        Assert.Null(await EditorDraftStorage.WriteAsync(storage, owner, "cluster", "peer", draft).ConfigureAwait(true));
        long repeatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.InRange(repeatedBytes, 0, 131072);
        script.FailWrites = true;
        Assert.NotNull(await EditorDraftStorage.WriteAsync(storage, owner, "cluster", "peer", draft).ConfigureAwait(true));
        script.FailWrites = false;
        script.Local.Clear();
        Assert.Null(await EditorDraftStorage.WriteAsync(storage, owner, "cluster", "peer", draft).ConfigureAwait(true));
        EditorDraft? recovered = await EditorDraftStorage.ReadAsync(storage, owner, "cluster", "peer", definition.Id).ConfigureAwait(true);
        Assert.Equal(ContractJson.Hash(draft), ContractJson.Hash(recovered));
        output.WriteLine(JsonSerializer.Serialize(new { payloadCharacters = 1048576, repeatedBytes }));
    }

    /// <summary>次の編集状態と送信前の未確定要求は、旧保存表現を流用せず復元できます。</summary>
    /// <returns>保存版、編集値、元の要求ID、未確定入力を区別した確認です。</returns>
    [Fact]
    public async Task ChangedDraftPreservesPendingRequestAndInvalidInputAsync()
    {
        BrowserStorage storage = new(new BrowserTestRuntime());
        Guid owner = Guid.NewGuid();
        WorkflowDefinition definition = new("flow", "original", [], []);
        string revision = ContractJson.Hash(definition);
        EditorDraft before = new(definition, definition, revision, definition, revision, revision, false, []);
        await EditorDraftStorage.WriteAsync(storage, owner, "cluster", "peer", before).ConfigureAwait(true);
        WorkflowDefinition edited = definition with { Name = "edited" };
        DraftChangeRequest request = new(Guid.NewGuid(), definition.Id, Guid.NewGuid(), revision, revision, definition, edited, new Dictionary<string, EEditResolution>());
        EditorDraft next = before with { Document = edited, Dirty = true, Pending = request, InputDrafts = new Dictionary<string, string> { ["n/inputs/value"] = "incomplete" } };
        Assert.Null(await EditorDraftStorage.WriteAsync(storage, owner, "cluster", "peer", next).ConfigureAwait(true));
        EditorDraft? recovered = await EditorDraftStorage.ReadAsync(storage, owner, "cluster", "peer", definition.Id).ConfigureAwait(true);
        Assert.Equal("original", recovered!.Base.Name);
        Assert.Equal("edited", recovered.Document.Name);
        Assert.Equal(request.OperationId, recovered.Pending!.OperationId);
        Assert.Equal("edited", recovered.Pending.Edited.Name);
        Assert.Equal("incomplete", recovered.InputDrafts!["n/inputs/value"]);
        Assert.True(recovered.Dirty);
        Assert.Null(await EditorDraftStorage.ReadAsync(storage, owner, "other", "peer", definition.Id).ConfigureAwait(true));
    }

    /// <summary>同じ入力検証結果だけでは変更や保存を発火せず、本当の入力変更は保持します。</summary>
    /// <returns>変更通知、dirty、未確定入力の遷移です。</returns>
    [Fact]
    public async Task UnchangedValidationDoesNotDirtyOrRepersistTheDocumentAsync()
    {
        WorkflowDefinition definition = new("flow", "original", [], []);
        EditorDocument document = new(new(new BrowserTestRuntime()));
        await document.LoadAsync(new(definition, ContractJson.Hash(definition), DateTimeOffset.UtcNow, EActivationPolicy.Auto)).ConfigureAwait(true);
        int changes = 0;
        document.Changed += () => changes++;
        document.SetInput("n/inputs/value", null);
        Assert.Equal(0, changes);
        Assert.False(document.Draft.Dirty);
        document.SetInput("n/inputs/value", "invalid");
        document.SetInput("n/inputs/value", "invalid");
        Assert.Equal(1, changes);
        Assert.True(document.HasInvalidInputs);
        document.SetInput("n/inputs/value", null);
        Assert.Equal(2, changes);
        Assert.False(document.HasInvalidInputs);
        Assert.True(document.Draft.Dirty);
    }
}
