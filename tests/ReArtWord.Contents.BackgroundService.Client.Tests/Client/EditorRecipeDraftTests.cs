using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>自動分離した草稿の保存を、参照順と接続先へ固定します。</summary>
public sealed class EditorRecipeDraftTests
{
    /// <summary>レシピ参照のない保存は、無関係な草稿の読込みや破損に依存しません。</summary>
    /// <returns>親草稿の退避を維持し、子定義を送信しない確認です。</returns>
    [Fact]
    public async Task RecipeFreeSaveDoesNotReadUnrelatedDraftsAsync()
    {
        BrowserTestRuntime script = new();
        BrowserStorage storage = new(script);
        EditorDocument model = new(storage);
        await model.SwitchTargetAsync("cluster", "peer").ConfigureAwait(true);
        WorkflowDefinition unrelated = Recipe("unrelated");
        await RetainAsync(storage, model, unrelated).ConfigureAwait(true);
        string key = "contents.blazor.draft." + model.StorageOwner.ToString("D") + "." + JsonSerializer.Serialize(new[] { "cluster", "peer", unrelated.Id });
        Assert.True(script.Local.ContainsKey(key));
        script.Local[key] = "invalid unrelated draft";
        model.Import(new("parent", "No recipe references", [], []));
        using ClientHttpHandler handler = new((_, _) => throw new InvalidOperationException("No child may be saved."));
        using HttpClient client = new(handler) { BaseAddress = new("http://localhost/") };
        EditorRecipeDrafts recipes = new(model, storage, new(client));
        await recipes.SaveDependenciesAsync(model.Draft.Document).ConfigureAwait(true);
        Assert.Equal("parent", await EditorDraftStorage.ReadLastAsync(storage, model.ClusterId, model.PeerId).ConfigureAwait(true));
        Assert.Equal("invalid unrelated draft", script.Local[key]);
    }

    /// <summary>無関係な草稿を保存せず、内側から順に一度だけ保存します。</summary>
    /// <returns>親の保存前に必要なレシピが解決される確認です。</returns>
    [Fact]
    public async Task SavesOnlyReferencedNewRecipesInDependencyOrderAsync()
    {
        BrowserStorage storage = new(new BrowserTestRuntime());
        EditorDocument model = new(storage);
        await model.SwitchTargetAsync("cluster", "peer").ConfigureAwait(true);
        WorkflowDefinition leaf = Recipe("leaf"), middle = Recipe("middle", "leaf"), unrelated = Recipe("unrelated");
        foreach (WorkflowDefinition recipe in new[] { middle, unrelated, leaf })
        {
            await RetainAsync(storage, model, recipe).ConfigureAwait(true);
        }
        model.Import(Recipe("parent", "middle") with { IsRecipe = false });
        List<string> saved = [];
        using ClientHttpHandler handler = new(async (request, cancellationToken) =>
        {
            WorkflowSaveRequest body = (await request.Content!.ReadFromJsonAsync<WorkflowSaveRequest>(ContractJson.Options, cancellationToken).ConfigureAwait(true))!;
            Assert.Equal("peer", Assert.Single(request.Headers.GetValues("X-Contents-Peer")));
            Assert.Equal("cluster", Assert.Single(request.Headers.GetValues("X-Contents-Cluster")));
            Assert.Null(body.ExpectedRevision);
            saved.Add(body.Definition.Id);
            return Success(body);
        });
        using HttpClient client = new(handler) { BaseAddress = new Uri("http://localhost/") };
        EditorRecipeDrafts recipes = new(model, storage, new(client));
        await recipes.SaveDependenciesAsync(model.Draft.Document).ConfigureAwait(true);
        Assert.Equal(2, saved.Count);
        Assert.Equal("leaf", saved[0]);
        Assert.Equal("middle", saved[1]);
        Assert.Equal(unrelated.Id, Assert.Single(recipes.Unsaved).Id);
        await recipes.SaveDependenciesAsync(model.Draft.Document).ConfigureAwait(true);
        Assert.Equal(2, saved.Count);
        Assert.Equal(model.Draft.Document.Id, await EditorDraftStorage.ReadLastAsync(storage, model.ClusterId, model.PeerId).ConfigureAwait(true));
    }

    /// <summary>応答欠落後の明示再操作も、同一内容には同一保存IDを使います。</summary>
    /// <returns>新規操作としての二重保存を発生させない確認です。</returns>
    [Fact]
    public async Task ReusesTheOperationIdAfterAnUnconfirmedSaveAsync()
    {
        BrowserStorage storage = new(new BrowserTestRuntime());
        EditorDocument model = new(storage);
        await model.SwitchTargetAsync("cluster", "peer").ConfigureAwait(true);
        await RetainAsync(storage, model, Recipe("child")).ConfigureAwait(true);
        model.Import(Recipe("parent", "child") with { IsRecipe = false });
        List<Guid> operations = [];
        using ClientHttpHandler handler = new(async (request, cancellationToken) =>
        {
            WorkflowSaveRequest body = (await request.Content!.ReadFromJsonAsync<WorkflowSaveRequest>(ContractJson.Options, cancellationToken).ConfigureAwait(true))!;
            operations.Add(body.OperationId);
            if (operations.Count == 1)
            {
                throw new HttpRequestException("Response lost");
            }
            return Success(body);
        });
        using HttpClient client = new(handler) { BaseAddress = new Uri("http://localhost/") };
        EditorRecipeDrafts recipes = new(model, storage, new(client));
        await Assert.ThrowsAsync<HttpRequestException>(() => recipes.SaveDependenciesAsync(model.Draft.Document)).ConfigureAwait(true);
        await recipes.SaveDependenciesAsync(model.Draft.Document).ConfigureAwait(true);
        Assert.Equal(2, operations.Count);
        Assert.Equal(operations[0], operations[1]);
    }

    /// <summary>新規の再利用定義を端末に保持します。</summary>
    /// <returns>書込み確認です。</returns>
    private static async Task RetainAsync(BrowserStorage storage, EditorDocument model, WorkflowDefinition recipe, CancellationToken cancellationToken = default)
    {
        EditorDraft draft = new(recipe, recipe, null, recipe, null, null, true, []);
        Assert.Null(await EditorDraftStorage.WriteAsync(storage, model.StorageOwner, model.ClusterId, model.PeerId, draft, cancellationToken).ConfigureAwait(true));
    }

    /// <summary>公開境界を持たないグループも草稿として再利用します。</summary>
    /// <returns>任意で一つの参照を持つ定義です。</returns>
    private static WorkflowDefinition Recipe(string id, string? target = null)
        => new(id, id, target is null ? []
                                      : [new("group", SubworkflowInterface.CallType(target), JsonSerializer.SerializeToElement(new { }))], []) { IsRecipe = true };

    /// <summary>各段階が確認済みの実管理APIと同じ応答です。</summary>
    /// <returns>型付き保存応答です。</returns>
    private static HttpResponseMessage Success(WorkflowSaveRequest request)
    {
        ConfigurationPhaseResult phase = new(EOutcome.Succeeded);
        WorkflowSaveResult result = new(request.OperationId, ContractJson.Hash(request.Definition), [], phase, phase, phase, phase);
        return new(HttpStatusCode.OK) { Content = JsonContent.Create(result, options: ContractJson.Options) };
    }
}
