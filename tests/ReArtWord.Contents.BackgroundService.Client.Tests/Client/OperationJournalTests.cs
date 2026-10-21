using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Operations.Journal;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>操作ごとの永続化と、副作用の前後に起きる保存障害を確認します。</summary>
public sealed class OperationJournalTests
{
    /// <summary>履歴を記録できない場合は送信せず、そのIDを案内します。</summary>
    /// <returns>送信前保存の確認です。</returns>
    [Fact]
    public async Task UnrecordedRequestIsNotSentAsync()
    {
        BrowserTestRuntime browser = new() { FailWrites = true };
        int sent = 0;
        using ClientHttpHandler handler = new((_, _) =>
        {
            sent++;
            return Task.FromResult(Reply(new { }));
        });
        using HttpClient http = Client(handler);
        OperationJournal journal = new(new(http), new(browser));
        Guid id = Guid.NewGuid();
        JSException error = await Assert.ThrowsAsync<JSException>(() => StartAsync(journal, id)).ConfigureAwait(true);
        Assert.Equal(id, Assert.Single(UiError.From(error)!.Arguments));
        Assert.Empty(journal.Items);
        Assert.Empty(browser.Operations);
        Assert.Equal(0, sent);
    }

    /// <summary>応答保存失敗後も元要求を照会でき、成功した副作用を再送しません。</summary>
    /// <returns>再読込みと元接続先への照会の確認です。</returns>
    [Fact]
    public async Task ResultStorageFailureKeepsOriginalRequestForReadOnlyRecoveryAsync()
    {
        BrowserTestRuntime browser = new();
        Guid id = Guid.NewGuid();
        int sent = 0, read = 0, changed = 0;
        using ClientHttpHandler handler = new((request, _) =>
        {
            Assert.Equal("peer-a", request.Headers.GetValues("X-Contents-Peer").Single());
            Assert.Equal("cluster-a", request.Headers.GetValues("X-Contents-Cluster").Single());
            if (request.Method == HttpMethod.Post)
            {
                sent++;
                Assert.Equal(id, Assert.Single(browser.Operations.Values).OperationId);
                browser.FailWrites = true;
            }
            else
            {
                read++;
            }
            return Task.FromResult(Reply(new { operationId = id, outcome = "succeeded", output = "confirmed" }));
        });
        using HttpClient http = Client(handler);
        ManagementApi api = new(http) { SelectedPeerId = "peer-a", SelectedClusterId = "cluster-a" };
        OperationJournal journal = new(api, new(browser));
        journal.Changed += () => changed++;
        JSException error = await Assert.ThrowsAsync<JSException>(() => StartAsync(journal, id)).ConfigureAwait(true);
        Assert.Equal(id, Assert.Single(UiError.From(error)!.Arguments));
        Assert.Equal("succeeded", OperationJournal.Outcome(journal.LastResult));
        Assert.Null(browser.Operations[id].Result);
        Assert.True(2 <= changed);
        browser.FailWrites = false;
        api.SelectedPeerId = "peer-b";
        api.SelectedClusterId = "cluster-b";
        OperationJournal restored = new(api, new(browser));
        await restored.LoadAsync().ConfigureAwait(true);
        await restored.ReadAsync(Assert.Single(restored.Items)).ConfigureAwait(true);
        Assert.Equal("succeeded", OperationJournal.Outcome(restored.LastResult));
        Assert.Equal(1, sent);
        Assert.Equal(1, read);
    }

    /// <summary>古いタブの更新で別操作の新しい結果を上書きせず、同じIDを再送しません。</summary>
    /// <returns>複数の履歴所有者による保存の確認です。</returns>
    [Fact]
    public async Task StaleJournalsUpdateOnlyTheirOperationAndRejectDuplicateStartAsync()
    {
        BrowserTestRuntime browser = new();
        int sent = 0;
        using ClientHttpHandler handler = new(async (request, token) =>
        {
            sent++;
            using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token).ConfigureAwait(false));
            return Reply(new { operationId = body.RootElement.GetProperty("operationId").GetGuid(), outcome = "succeeded" });
        });
        using HttpClient http = Client(handler);
        OperationJournal first = new(new(http), new(browser)), second = new(new(http), new(browser));
        await Task.WhenAll(first.LoadAsync(), second.LoadAsync()).ConfigureAwait(true);
        Guid one = Guid.NewGuid(), two = Guid.NewGuid();
        await StartAsync(first, one).ConfigureAwait(true);
        await StartAsync(second, two).ConfigureAwait(true);
        await Assert.ThrowsAsync<JSException>(() => StartAsync(second, one)).ConfigureAwait(true);
        OperationJournal restored = new(new(http), new(browser));
        await restored.LoadAsync().ConfigureAwait(true);
        Assert.Equal(2, restored.Items.Count);
        Assert.All(restored.Items, item => Assert.Equal("succeeded", OperationJournal.Outcome(item.Result)));
        Assert.Equal(2, sent);
    }

    /// <summary>別タブで開始した操作を追跡しても、空の要求や現在選択中のPeerで上書きしません。</summary>
    /// <returns>元要求・接続先・回復先の確認です。</returns>
    [Fact]
    public async Task TrackingPreservesOtherTabsRecoveryRequestAndTargetAsync()
    {
        BrowserTestRuntime browser = new();
        Guid id = Guid.NewGuid();
        using ClientHttpHandler handler = new((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("original", request.Headers.GetValues("X-Contents-Peer").Single());
            return Task.FromResult(Reply(new { operationId = id, outcome = "unknown" }));
        });
        using HttpClient http = Client(handler);
        OperationJournal journal = new(new(http) { SelectedPeerId = "other" }, new(browser));
        await journal.LoadAsync().ConfigureAwait(true);
        OperationRecord original = new(id, "restore", "/api/snapshots/restores/" + id, "/api/snapshots/recover",
            JsonSerializer.SerializeToElement(new { operationId = id, snapshot = "original-snapshot" }), DateTimeOffset.UtcNow, PeerId: "original");
        browser.Operations.Add(id, original);
        await journal.TrackAsync("restore", id).ConfigureAwait(true);
        OperationRecord tracked = Assert.Single(journal.Items);
        Assert.Equal(original.Request.GetRawText(), tracked.Request.GetRawText());
        Assert.Equal(original.RecoverPath, tracked.RecoverPath);
        Assert.Equal(original.PeerId, tracked.PeerId);
    }

    /// <summary>大きな応答を受けても、履歴以外のlocalStorageへ書き戻しません。</summary>
    /// <returns>要求と応答を欠落なく保持した確認です。</returns>
    [Fact]
    public async Task LargeResultDoesNotDependOnLocalStorageWritesAsync()
    {
        string payload = new('x', 6 * 1024 * 1024), operationKey = "contents.operation-journal";
        BrowserTestRuntime browser = new() { RejectWrite = (key, _) => !string.Equals(key, operationKey, StringComparison.Ordinal) };
        Guid id = Guid.NewGuid();
        using ClientHttpHandler handler = new((_, _) => Task.FromResult(Reply(new { operationId = id, outcome = "succeeded", payload })));
        using HttpClient http = Client(handler);
        OperationJournal journal = new(new(http), new(browser));
        await StartAsync(journal, id).ConfigureAwait(true);
        OperationJournal restored = new(new(http), new(browser));
        await restored.LoadAsync().ConfigureAwait(true);
        Assert.Equal(payload, Assert.Single(restored.Items).Result!.Value.GetProperty("payload").GetString());
        Assert.Empty(browser.Local);
    }

    /// <summary>保存待機中に次操作が追加されても、応答を別の行へ割り当てません。</summary>
    /// <returns>同じ画面の並行操作の確認です。</returns>
    [Fact]
    public async Task PendingResultWriteDoesNotReplaceAnotherOperationAsync()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        BrowserTestRuntime browser = new() { OperationUpdateDelay = release.Task };
        using ClientHttpHandler handler = new(async (request, token) =>
        {
            using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token).ConfigureAwait(false));
            return Reply(new { operationId = body.RootElement.GetProperty("operationId").GetGuid(), outcome = "succeeded" });
        });
        using HttpClient http = Client(handler);
        OperationJournal journal = new(new(http), new(browser));
        Task first = StartAsync(journal, Guid.NewGuid());
        Task second = StartAsync(journal, Guid.NewGuid());
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        release.SetResult();
        await Task.WhenAll(first, second).ConfigureAwait(true);
        Assert.Equal(2, journal.Items.Select(item => item.OperationId).Distinct().Count());
        Assert.All(journal.Items, item => Assert.Equal(item.OperationId, item.Result!.Value.GetProperty("operationId").GetGuid()));
    }

    /// <summary>元IDを本文にも保持した試験要求です。</summary>
    /// <returns>副作用要求の応答です。</returns>
    private static Task<JsonElement> StartAsync(OperationJournal journal, Guid id, CancellationToken cancellationToken = default) =>
        journal.StartAsync(id, "run", "/api/runs", "/api/runs/" + id, new { operationId = id }, cancellationToken: cancellationToken);

    /// <summary>試験境界のHTTPクライアントです。</summary>
    /// <returns>呼出元が所有するクライアントです。</returns>
    private static HttpClient Client(HttpMessageHandler handler) => new(handler, false) { BaseAddress = new Uri("http://localhost/") };

    /// <summary>実際のシリアライズ規則に従う応答です。</summary>
    /// <returns>呼出元が所有するHTTP応答です。</returns>
    private static HttpResponseMessage Reply<T>(T value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value, ContractJson.Options), Encoding.UTF8, "application/json") };
}
