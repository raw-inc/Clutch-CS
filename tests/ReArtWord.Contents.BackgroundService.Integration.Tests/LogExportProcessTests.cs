using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>独立したCoreとClientHostを使い、検索から標準HTTPダウンロードまで確認します。</summary>
public sealed class LogExportProcessTests
{
    /// <summary>未選択でも複数クラスタのログを取得し、論理名・元データ・検索条件をZIPへ含めます。</summary>
    /// <returns>実プロセス・UDP管理通信・HTTP逐次送信の確認です。</returns>
    [Fact]
    public async Task AllObservedClustersExportNamedFilteredLogsAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(2));
        int port = CorePeer.AvailablePort();
        CorePeer first = new(Guid.NewGuid().ToString("N"), "a", port, true), second = new(Guid.NewGuid().ToString("N"), "a", port, true);
        ClientHostProcess client = new(Path.Combine(first.DirectoryPath, "export-client"));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = ((IAsyncDisposable)first).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = ((IAsyncDisposable)second).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable clientLifetime = ((IAsyncDisposable)client).ConfigureAwait(true);
        await first.StartAsync(timeout.Token);
        await second.StartAsync(timeout.Token);
        await WriteLogAsync(first, "一番目のWF", timeout.Token);
        await WriteLogAsync(second, "二番目のWF", timeout.Token);
        await client.StartAsync(timeout.Token);
        using (HttpResponseMessage binding = await client.SendAsync(HttpMethod.Post, "client/network", new ClientNetworkBinding(first.InterfaceId, "127.0.0.1", "127.255.255.255", port), cancellationToken: timeout.Token))
        {
            binding.EnsureSuccessStatusCode();
        }
        await CorePeer.WaitAsync(async token =>
        {
            using HttpResponseMessage response = await client.SendAsync(HttpMethod.Get, "client/clusters", cancellationToken: token).ConfigureAwait(false);
            DiscoveredCluster[]? clusters = await response.Content.ReadFromJsonAsync<DiscoveredCluster[]>(ContractJson.Options, token).ConfigureAwait(false);
            return clusters?.Length == 2 && clusters.All(cluster => cluster.Peers.Count == 1);
        }, timeout.Token);
        LogExportRequest request = new(new(WorkflowId: "export-workflow", NodeId: "record", Levels: ["WARN"], Text: "export-marker", UncompressedOnly: true), "Asia/Tokyo");
        using HttpResponseMessage receipt = await client.SendAsync(HttpMethod.Post, "client/log-exports/", request, cancellationToken: timeout.Token);
        receipt.EnsureSuccessStatusCode();
        LogExportStatus job = (await receipt.Content.ReadFromJsonAsync<LogExportStatus>(ContractJson.Options, timeout.Token))!;
        Assert.Equal("queued", job.State);
        using HttpClient browser = new() { BaseAddress = client.Address };
        using HttpRequestMessage download = new(HttpMethod.Get, "client/log-exports/" + job.Id + "/download");
        using HttpResponseMessage streamed = await browser.SendAsync(download, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        streamed.EnsureSuccessStatusCode();
        Assert.Equal("application/zip", streamed.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", streamed.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Null(streamed.Content.Headers.ContentLength);
        using Stream stream = await streamed.Content.ReadAsStreamAsync(timeout.Token);
        using MemoryStream captured = new();
        await stream.CopyToAsync(captured, timeout.Token);
        captured.Position = 0;
        using ZipArchive zip = new(captured, ZipArchiveMode.Read, leaveOpen: true);
        using StreamReader text = new(await zip.GetEntry("logs-00001.txt")!.OpenAsync(timeout.Token));
        string lines = await text.ReadToEndAsync(timeout.Token);
        Assert.Contains("[WARN] 一番目のWF 記録ボタン export-marker", lines, StringComparison.Ordinal);
        Assert.Contains("[WARN] 二番目のWF 記録ボタン export-marker", lines, StringComparison.Ordinal);
        Assert.Contains("+09:00", lines, StringComparison.Ordinal);
        using Stream detailStream = await zip.GetEntry("logs-00001.json")!.OpenAsync(timeout.Token);
        using JsonDocument details = await JsonDocument.ParseAsync(detailStream, cancellationToken: timeout.Token);
        JsonElement[] entries = details.RootElement.GetProperty("entries").EnumerateArray().ToArray();
        Assert.Equal(2, entries.Length);
        Assert.Equal(2, entries.Select(entry => entry.GetProperty("clusterId").GetString()).Distinct().Count());
        Assert.All(entries, entry =>
        {
            Assert.True(entry.GetProperty("namesResolved").GetBoolean());
            Assert.Equal(42, entry.GetProperty("log").GetProperty("data").GetProperty("json").GetProperty("value").GetInt32());
        });
        using HttpResponseMessage status = await client.SendAsync(HttpMethod.Get, "client/log-exports/" + job.Id, cancellationToken: timeout.Token);
        LogExportStatus completed = (await status.Content.ReadFromJsonAsync<LogExportStatus>(ContractJson.Options, timeout.Token))!;
        Assert.Equal("completed", completed.State);
        Assert.True(completed.Complete);
        Assert.Equal(2, completed.Entries);
        using HttpResponseMessage repeated = await client.SendAsync(HttpMethod.Get, "client/log-exports/" + job.Id + "/download", cancellationToken: timeout.Token);
        Assert.Equal(HttpStatusCode.NotFound, repeated.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(first.DirectoryPath, "export-client", "log-exports")));
    }

    /// <summary>隔離Coreに一件だけ実行し、名前を持つログの永続化を確認します。</summary>
    /// <returns>保存・実行・ログ書込みが完了した状態です。</returns>
    private static async Task WriteLogAsync(CorePeer core, string name, CancellationToken cancellationToken = default)
    {
        await CorePeer.WaitAsync(async token => (await core.GetAsync<JsonElement>("health", token).ConfigureAwait(false)).GetProperty("ready").GetBoolean(), cancellationToken).ConfigureAwait(false);
        WorkflowDefinition workflow = new("export-workflow", name,
            [new("record", "control.log", JsonSerializer.SerializeToElement(new { level = "WARN", message = "export-marker" }),
                new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(new { value = 42 }) }, LogicalName: "記録ボタン")], []);
        WorkflowSaveResult saved = await core.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), workflow, null), cancellationToken).ConfigureAwait(false);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        WorkflowRunRequest request = new(Guid.NewGuid(), workflow.Id, saved.Revision);
        await core.PostAsync<WorkflowRunStatus>("runs.start", request, cancellationToken).ConfigureAwait(false);
        await CorePeer.WaitAsync(async token =>
        {
            LogQueryResult logs = await core.GetAsync<LogQueryResult>("logs.query", new LogQuery(WorkflowId: workflow.Id, NodeId: "record", Text: "export-marker"), token).ConfigureAwait(false);
            return logs.Entries.Count == 1;
        }, cancellationToken).ConfigureAwait(false);
    }
}
