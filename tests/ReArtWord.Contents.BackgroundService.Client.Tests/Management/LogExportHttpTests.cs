using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReArtWord.Contents.BackgroundService.ClientHost.Logs;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Management;

/// <summary>実HTTPの切断が管理検索を停止することを確認します。</summary>
public sealed class LogExportHttpTests
{
    /// <summary>ZIP先頭を受信後にTCP接続を閉じ、取得中の取消と次の受付を確認します。</summary>
    /// <returns>要求境界と検索境界の取消連結です。</returns>
    // 抑制理由: JobとWebApplicationは直後のDIコンテナー・非同期寿命へ所有権を移します。
    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW141", Justification = "DIコンテナーとConfiguredAsyncDisposableが資源の所有者です。"),
     System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "JobsはHostへ登録してHost終了時に破棄します。")]
    public async Task DisconnectCancelsSearchAndCrossSiteDownloadIsRejectedAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously), ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        LogExportJobs jobs = new(() => [new DiscoveredCluster("cluster",
            [new("peer", Guid.NewGuid(), "host", "127.0.0.1", 1234, EPeerStatus.Ready, 1, null, DateTimeOffset.UtcNow)], [], DateTimeOffset.UtcNow)],
            async (_, _, token) =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                    return null;
                }
                finally
                {
                    ended.TrySetResult();
                }
            }, (_, _, _) => throw new InvalidOperationException("Names are not reached."));
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(jobs);
        builder.Services.AddHostedService(provider => provider.GetRequiredService<LogExportJobs>());
        WebApplication app = builder.Build();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable appLifetime = app.ConfigureAwait(true);
        LogExportRoutes.Map(app);
        await app.StartAsync(timeout.Token);
        try
        {
            Guid id = jobs.Create(new(new())).Id;
            using HttpClient client = new() { BaseAddress = new Uri(Assert.Single(app.Urls)) };
            string url = "/client/log-exports/" + id + "/download";
            using (HttpRequestMessage external = new(HttpMethod.Get, url))
            {
                external.Headers.Add("Sec-Fetch-Site", "cross-site");
                using HttpResponseMessage refused = await client.SendAsync(external, timeout.Token);
                Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            }
            using (HttpRequestMessage request = new(HttpMethod.Get, url))
            using (HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token))
            {
                response.EnsureSuccessStatusCode();
                await started.Task.WaitAsync(timeout.Token);
                using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                byte[] header = new byte[4];
                await stream.ReadExactlyAsync(header, timeout.Token);
                Assert.Equal((byte)'P', header[0]);
                Assert.Equal((byte)'K', header[1]);
            }
            await ended.Task.WaitAsync(timeout.Token);
            string running = "running";
            while (string.Equals(jobs.Read(id)?.State, running, StringComparison.Ordinal))
            {
                await Task.Delay(10, timeout.Token);
            }
            Assert.Equal("cancelled", jobs.Read(id)!.State);
        }
        finally
        {
            await app.StopAsync(timeout.Token);
        }
    }
}
