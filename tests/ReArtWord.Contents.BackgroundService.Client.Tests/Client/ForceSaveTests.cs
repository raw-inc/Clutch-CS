using System.Net;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.Management;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>強制保存も最新確認・端末退避・版競合・結果不明の非再送を守ります。</summary>
public sealed class ForceSaveTests
{
    /// <summary>再読み込み後も保存済み要求を検出し、POSTではなく元結果を照会します。</summary>
    /// <returns>ブラウザー寿命をまたぐ非再送です。</returns>
    [Fact]
    public async Task ReloadFindsAnUnconfirmedSaveBeforeAcceptingAnotherAsync()
    {
        using Scenario scenario = new() { LoseResponse = true };
        await scenario.InitializeAsync().ConfigureAwait(true);
        await scenario.Force.PrepareAsync().ConfigureAwait(true);
        await Assert.ThrowsAsync<HttpRequestException>(() => scenario.Force.SaveAsync(EActivationPolicy.Explicit)).ConfigureAwait(true);
        BrowserStorage storage = new(scenario.Browser);
        EditorForceSave restored = new(scenario.Model, scenario.Api, storage, new(scenario.Api, new(scenario.Browser)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => restored.PrepareAsync()).ConfigureAwait(true);
        Assert.Equal(scenario.Force.OperationId, restored.OperationId);
        await restored.LookupAsync().ConfigureAwait(true);
        Assert.Single(scenario.Requests);
        Assert.False(scenario.Model.Draft.Dirty);
        Guid? reconciliation = scenario.Model.Draft.Pending?.OperationId;
        await restored.LookupAsync().ConfigureAwait(true);
        Assert.Equal(reconciliation, scenario.Model.Draft.Pending?.OperationId);
    }

    /// <summary>古い草稿基準ではなく確認した最新保存版へ、退避後に一度だけ保存します。</summary>
    /// <returns>正常な明示上書きです。</returns>
    [Fact]
    public async Task ForceSaveBacksUpAndUsesTheReviewedRevisionAsync()
    {
        using Scenario scenario = new();
        await scenario.InitializeAsync().ConfigureAwait(true);
        await scenario.Force.PrepareAsync().ConfigureAwait(true);
        Assert.Equal("current", scenario.Force.Review!.Saved!.Revision);
        Assert.NotEmpty(scenario.Force.Review.Differences);
        WorkflowSaveResult? saved = await scenario.Force.SaveAsync(EActivationPolicy.Explicit).ConfigureAwait(true);
        WorkflowSaveRequest request = Assert.Single(scenario.Requests);
        Assert.Equal("current", request.ExpectedRevision);
        Assert.Equal("local", request.Definition.Name);
        Assert.Equal(EActivationPolicy.Explicit, request.ActivationPolicy);
        Assert.Equal(saved!.Revision, scenario.Model.Draft.BaseRevision);
        Assert.False(scenario.Model.Draft.Dirty);
    }

    /// <summary>確認後に変わった接続・草稿・保存版へ、同じ確認を流用しません。</summary>
    /// <returns>送信前の停止です。</returns>
    [Theory]
    [InlineData("draft")]
    [InlineData("connection")]
    [InlineData("revision")]
    public async Task ChangedReviewDoesNotSendAsync(string change)
    {
        using Scenario scenario = new();
        await scenario.InitializeAsync().ConfigureAwait(true);
        await scenario.Force.PrepareAsync().ConfigureAwait(true);
        switch (change)
        {
            case "draft": scenario.Model.Change(document => document with { Name = "later" }); break;
            case "connection": scenario.Api.SelectedClusterId = "other"; break;
            default: scenario.Current = scenario.Current with { Revision = "later" }; break;
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.Force.SaveAsync(EActivationPolicy.Explicit)).ConfigureAwait(true);
        Assert.Empty(scenario.Requests);
        Assert.True(scenario.Model.Draft.Dirty);
    }

    /// <summary>送信後の応答喪失は元IDの照会だけで回復します。</summary>
    /// <returns>POSTを重複させない確認です。</returns>
    [Fact]
    public async Task LostResponseIsLookedUpWithoutRepeatingTheSaveAsync()
    {
        using Scenario scenario = new() { LoseResponse = true };
        await scenario.InitializeAsync().ConfigureAwait(true);
        await scenario.Force.PrepareAsync().ConfigureAwait(true);
        await Assert.ThrowsAsync<HttpRequestException>(() => scenario.Force.SaveAsync(EActivationPolicy.Explicit)).ConfigureAwait(true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.Force.PrepareAsync()).ConfigureAwait(true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.Force.SaveAsync(EActivationPolicy.Explicit)).ConfigureAwait(true);
        WorkflowSaveResult? recovered = await scenario.Force.LookupAsync().ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, recovered!.Saved.Outcome);
        Assert.Single(scenario.Requests);
        Assert.False(scenario.Model.Draft.Dirty);
    }

    /// <summary>型検証失敗と退避失敗を、強制指定で無視しません。</summary>
    /// <returns>保存要求を送らない境界です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidOrUnprotectedDraftDoesNotSendAsync(bool storageFailure)
    {
        using Scenario scenario = new();
        await scenario.InitializeAsync().ConfigureAwait(true);
        await scenario.Force.PrepareAsync().ConfigureAwait(true);
        scenario.Browser.FailWrites = storageFailure;
        scenario.Valid = storageFailure;
        Exception? failure = await Record.ExceptionAsync(() => scenario.Force.SaveAsync(EActivationPolicy.Explicit)).ConfigureAwait(true);
        Assert.NotNull(failure);
        Assert.Empty(scenario.Requests);
        Assert.Null(scenario.Force.OperationId);
    }

    /// <summary>保存応答が返るまでに追加した編集を、送信した版で消しません。</summary>
    /// <returns>保存版と未保存の追加編集を区別した状態です。</returns>
    [Fact]
    public async Task EditsMadeAfterSendingRemainDirtyAsync()
    {
        using Scenario scenario = new() { EditDuringSave = true };
        await scenario.InitializeAsync().ConfigureAwait(true);
        await scenario.Force.PrepareAsync().ConfigureAwait(true);
        await scenario.Force.SaveAsync(EActivationPolicy.Explicit).ConfigureAwait(true);
        Assert.Equal("local", scenario.Model.Draft.Base.Name);
        Assert.Equal("continued", scenario.Model.Draft.Document.Name);
        Assert.True(scenario.Model.Draft.Dirty);
    }

    /// <summary>保存APIとブラウザー退避を実際のClient実装へ接続します。</summary>
    private sealed class Scenario : IDisposable
    {
        private readonly HttpClient client;
        private readonly ClientHttpHandler handler;
        private WorkflowSaveResult? lastResult;
        internal BrowserTestRuntime Browser
        {
            get;
        } = new();

        internal EditorDocument Model
        {
            get;
        }

        internal ManagementApi Api
        {
            get;
        }

        internal EditorForceSave Force
        {
            get;
        }

        internal StoredWorkflow Current
        {
            get; set;
        } = new(new("flow", "remote", [], []), "current", DateTimeOffset.UtcNow, EActivationPolicy.Auto);

        internal List<WorkflowSaveRequest> Requests
        {
            get;
        } = [];

        internal bool LoseResponse
        {
            get; init;
        }

        internal bool EditDuringSave
        {
            get; init;
        }

        internal bool Valid
        {
            get; set;
        } = true;

        /// <summary>使い捨てのHTTP処理と保存領域を構成します。</summary>
        internal Scenario()
        {
            handler = new(SendAsync);
            client = new(handler, false) { BaseAddress = new Uri("http://localhost/") };
            BrowserStorage storage = new(Browser);
            Api = new(client) { SelectedClusterId = "cluster", SelectedPeerId = "peer" };
            Model = new(storage);
            Force = new(Model, Api, storage, new(Api, new(Browser)));
        }

        /// <summary>最新とは異なる保存版からのローカル編集を用意します。</summary>
        /// <returns>草稿の初期化です。</returns>
        internal async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            await Model.SwitchTargetAsync("cluster", "peer", cancellationToken).ConfigureAwait(false);
            await Model.LoadAsync(Current with { Revision = "old" }, cancellationToken).ConfigureAwait(false);
            Model.Change(document => document with { Name = "local" });
        }

        /// <summary>保存・検証・照会を分離し、送信前の退避を観測します。</summary>
        /// <returns>製品契約に沿った応答です。</returns>
        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
        {
            string path = request.RequestUri!.AbsolutePath;
            string save = "/api/workflows/save", validate = "/api/workflows/validate", operation = "/api/workflows/operations/";
            if (string.Equals(path, validate, StringComparison.Ordinal))
            {
                return Reply(new ValidationView(Valid, [], null));
            }
            if (path.StartsWith(operation, StringComparison.Ordinal))
            {
                return Reply(lastResult);
            }
            if (!string.Equals(path, save, StringComparison.Ordinal))
            {
                return Reply(new WorkflowView(Current, Current.Definition, Current.Revision));
            }
            Assert.True(Browser.Local.ContainsKey("contents.blazor.force-save.backup"));
            WorkflowSaveRequest input = JsonSerializer.Deserialize<WorkflowSaveRequest>(await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), ContractJson.Options)!;
            Assert.Equal(Current.Revision, input.ExpectedRevision);
            Requests.Add(input);
            ConfigurationPhaseResult success = new(EOutcome.Succeeded);
            lastResult = new(input.OperationId, "saved", [], success, success, success, success);
            if (EditDuringSave)
            {
                Model.Change(document => document with { Name = "continued" });
            }
            if (LoseResponse)
            {
                throw new HttpRequestException("response lost");
            }
            return Reply(lastResult);
        }

        /// <summary>製品JSONで応答します。</summary>
        /// <returns>呼出元が破棄するHTTP応答です。</returns>
        private static HttpResponseMessage Reply<T>(T value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value, ContractJson.Options), Encoding.UTF8, "application/json") };

        /// <summary>HTTPクライアントと所有handlerを解放します。</summary>
        public void Dispose()
        {
            client.Dispose();
            handler.Dispose();
        }
    }
}
