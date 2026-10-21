using System.Net;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.Operations.Configuration;
using ReArtWord.Contents.BackgroundService.Client.Operations.Journal;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster.Conflicts;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>競合解消の確認集合・秘密値・保存用データを維持します。</summary>
public sealed class ConflictFormTests
{
    /// <summary>秘密候補は生値を持たず、全版を確認した明示選択だけを要求へ残します。</summary>
    [Fact]
    public void SecretSelectionKeepsAllVersionsWithoutValues()
    {
        StateConflict conflict = new("id", "secrets", "api", [new("one", null), new("two", null)], [], [], true, true);
        ConflictResolveRequest request = ConflictPanel.BuildRequest(conflict, "two", true, false, string.Empty);
        Assert.Equal(["one", "two"], request.ExpectedVersions);
        Assert.Equal("two", request.SelectedVersion);
        Assert.Null(request.MergedValue);
        Assert.False(request.UseMergedValue);
        Assert.NotEqual(Guid.Empty, request.OperationId);
        Assert.Throws<InvalidOperationException>(() => ConflictPanel.BuildRequest(conflict, "two", false, false, string.Empty));
        Assert.Throws<InvalidOperationException>(() => ConflictPanel.BuildRequest(conflict, "two", true, true, "\"secret\""));
        Assert.Throws<InvalidOperationException>(() => ConflictPanel.BuildRequest(conflict, "unknown", true, false, string.Empty));
    }

    /// <summary>定義編集の新ハッシュと元の保存方針を同じ外側のデータ構造に保持します。</summary>
    [Fact]
    public void WorkflowEditRecalculatesRevisionAndPreservesEnvelope()
    {
        WorkflowDefinition definition = new("flow", "before", [], []);
        StoredWorkflow saved = new(definition, ContractJson.Hash(definition), DateTimeOffset.UnixEpoch, EActivationPolicy.Explicit);
        StateConflict conflict = Conflict("workflows", NodeValue.FromObject(saved));
        WorkflowDefinition edited = definition with { Name = "after" };
        ConflictResolveRequest request = ConflictPanel.BuildRequest(conflict, "one", true, true, JsonSerializer.Serialize(edited, ContractJson.Options));
        StoredWorkflow actual = request.MergedValue!.Json.Deserialize<StoredWorkflow>(ContractJson.Options)!;
        Assert.Equal("after", actual.Definition.Name);
        Assert.Equal(ContractJson.Hash(edited), actual.Revision);
        Assert.Equal(saved.SavedAt, actual.SavedAt);
        Assert.Equal(EActivationPolicy.Explicit, actual.ActivationPolicy);
        Assert.True(request.UseMergedValue);
        Assert.Null(request.SelectedVersion);
        Assert.Throws<InvalidOperationException>(() => ConflictPanel.BuildRequest(conflict, "one", true, true, JsonSerializer.Serialize(edited with { Id = "other" }, ContractJson.Options)));
    }

    /// <summary>共通設定も内容ハッシュを再計算し、状態JSON nullを削除に変換しません。</summary>
    [Fact]
    public void OperationalEditAndStateNullHaveExplicitMeaning()
    {
        ClusterOperationalSettings settings = new("UTC", EFileConflictPolicy.Fail, EFileInternalFailurePolicy.Keep, new Dictionary<string, IReadOnlyList<string>>());
        ClusterOperationalSnapshot snapshot = new(settings, ContractJson.Hash(settings), DateTimeOffset.UnixEpoch);
        StateConflict conflict = Conflict("cluster.operational", NodeValue.FromObject(new { Snapshot = snapshot, ActivationPolicy = EActivationPolicy.Explicit }));
        ClusterOperationalSettings changed = settings with { FileConflictPolicy = EFileConflictPolicy.Wait };
        ConflictResolveRequest request = ConflictPanel.BuildRequest(conflict, "one", true, true, JsonSerializer.Serialize(changed, ContractJson.Options));
        JsonProperty savedSnapshot = request.MergedValue!.Json.EnumerateObject().Single(item => string.Equals(item.Name, "snapshot", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ContractJson.Hash(changed), savedSnapshot.Value.Deserialize<ClusterOperationalSnapshot>(ContractJson.Options)!.Revision);
        ConflictResolveRequest state = ConflictPanel.BuildRequest(Conflict("workflow:flow", NodeValue.FromObject(1)), "one", true, true, "null");
        Assert.NotNull(state.MergedValue);
        Assert.Equal(JsonValueKind.Null, state.MergedValue.Json.ValueKind);
        Assert.ThrowsAny<JsonException>(() => ConflictPanel.BuildRequest(conflict, "one", true, true, "{"));
    }

    /// <summary>競合操作は保存・共有・スナップショット・適用をそれぞれ確認します。</summary>
    [Theory]
    [InlineData(EOutcome.Succeeded, "succeeded")]
    [InlineData(EOutcome.Unknown, "unknown")]
    [InlineData(EOutcome.Failed, "partial")]
    public void FourPhaseOutcomeRemainsExplicit(EOutcome shared, string expected)
    {
        ConflictResolveResult result = new(Guid.NewGuid(), new(EOutcome.Succeeded), new(shared), new(EOutcome.Succeeded), [], new(EOutcome.Succeeded));
        Assert.Equal(expected, OperationJournal.Outcome(JsonSerializer.SerializeToElement(result, ContractJson.Options)));
    }

    /// <summary>操作IDのない成功形応答を確定せず、元要求を端末に保持します。</summary>
    /// <returns>送信前保存と再照会の確認です。</returns>
    [Fact]
    public async Task MissingOperationIdentityRemainsUnknownAndPreservesRequestAsync()
    {
        int sent = 0;
        BrowserTestRuntime browser = new();
        using ClientHttpHandler handler = new((_, _) =>
        {
            sent++;
            Assert.Single(browser.Operations);
            string body = JsonSerializer.Serialize(new { saved = new { outcome = "succeeded" }, shared = new { outcome = "succeeded" }, activation = new { outcome = "succeeded" }, issues = Array.Empty<object>() });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        });
        using HttpClient client = new(handler) { BaseAddress = new Uri("http://127.0.0.1") };
        OperationJournal journal = new(new(client), new(browser));
        Guid id = Guid.NewGuid();
        string[] versions = ["one", "two"];
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.StartAsync(id, "conflict", "/api/conflicts/resolve", "/api/conflicts/operations/" + id, new { operationId = id, conflictId = "fork", expectedVersions = versions, selectedVersion = "one" })).ConfigureAwait(true);
        Assert.Equal("unknown", OperationJournal.Outcome(journal.Items.Single().Result));
        Assert.Equal("two", journal.Items.Single().Request.GetProperty("expectedVersions")[1].GetString());
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.TrackAsync("restore", id)).ConfigureAwait(true);
        Assert.Equal(1, sent);
    }

    /// <summary>公開された比較可能候補を構成します。</summary>
    /// <returns>一つの既存版を含む競合です。</returns>
    private static StateConflict Conflict(string scope, NodeValue value) => new("id", scope, "flow", [new("one", value)], [], [], false, true);
}
