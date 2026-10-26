using System.IO.Compression;
using ReArtWord.Contents.BackgroundService.ClientHost.Logs;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Management;

/// <summary>接続寿命・手動取消・停止と出力受付の所有関係を検証します。</summary>
public sealed class LogExportJobTests
{
    /// <summary>バックグラウンド処理の開始が遅れても、停止通知の準備前に起動完了を返しません。</summary>
    /// <returns>起動直後の書き出しにも停止通知が届くことを保証する起動順序です。</returns>
    [Fact]
    public async Task StartupWaitsForShutdownRegistrationAsync()
    {
        using ManualResetEventSlim release = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int deferred = 0;

        // ExecuteAsyncの開始だけを遅らせ、ThreadPool全体の設定や製品コードの試験用フックを使いません。
        AsyncLocal<bool> defer = new(change =>
        {
            if (change.ThreadContextChanged && change.CurrentValue && Interlocked.CompareExchange(ref deferred, 1, 0) == 0)
            {
                entered.TrySetResult();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        });
        using LogExportJobs jobs = new(Clusters, (_, _, _) => Task.FromResult<LogQueryResult?>(new([], null, true)), NamesAsync);
        Task startup;
        defer.Value = true;
        try
        {
            startup = jobs.StartAsync(CancellationToken.None);
        }
        finally
        {
            defer.Value = false;
        }
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(startup.IsCompleted);
        }
        finally
        {
            release.Set();
            await startup.WaitAsync(TimeSpan.FromSeconds(5));
            await jobs.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Throws<OperationCanceledException>(() => jobs.Create(new(new())));
    }

    /// <summary>未開始受付と送信状態を期限で解放し、完了ファイルの再生成を許可しません。</summary>
    /// <returns>全件保持なしの一回限りの送信です。</returns>
    [Theory]
    [InlineData(ELogExportFormat.Zip)]
    [InlineData(ELogExportFormat.Log)]
    public async Task ReceiptsExpireAndDownloadsAreSingleUseAsync(ELogExportFormat format)
    {
        ExportClock clock = new();
        DateTimeOffset? until = null;
        using LogExportJobs jobs = new(Clusters, (_, query, _) =>
        {
            until = query.Until;
            return Task.FromResult<LogQueryResult?>(new([], null, true));
        }, NamesAsync, clock);
        Assert.Throws<ArgumentException>(() => jobs.Create(new(new()) { Format = (ELogExportFormat)123 }));
        LogExportStatus created = jobs.Create(new(new(), "UTC") { Format = format });
        Assert.Equal(format, created.Format);
        Assert.Throws<InvalidOperationException>(() => jobs.Create(new(new())));
        using MemoryStream stream = new();
        await jobs.WriteAsync(created.Id, stream);
        Assert.Equal(clock.GetUtcNow(), until);
        Assert.Equal("completed", jobs.Read(created.Id)!.State);
        Assert.True(jobs.Read(created.Id)!.Complete);
        Assert.True(stream.CanWrite);
        stream.Position = 0;
        if (format == ELogExportFormat.Zip)
        {
            using ZipArchive archive = new(stream, ZipArchiveMode.Read, leaveOpen: true);
            Assert.Equal(4, archive.Entries.Count);
        }
        else
        {
            using StreamReader reader = new(stream, leaveOpen: true);
            string content = await reader.ReadToEndAsync();
            Assert.Contains("# Summary: ", content, StringComparison.Ordinal);
            Assert.Contains("\"entries\":0", content, StringComparison.Ordinal);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => jobs.WriteAsync(created.Id, stream));
        clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Null(jobs.Read(created.Id));
        LogExportStatus pending = jobs.Create(new(new(), "UTC", new("cluster", "peer")));
        clock.Advance(TimeSpan.FromMinutes(3));
        await Assert.ThrowsAsync<InvalidOperationException>(() => jobs.WriteAsync(pending.Id, stream));
        LogExportStatus next = jobs.Create(new(new()));
        await jobs.CancelAsync(next.Id);
        Assert.Equal("cancelled", jobs.Read(next.Id)!.State);
    }

    /// <summary>接続断・明示取消・Host停止が取得処理まで伝わり、完了扱いを残しません。</summary>
    /// <returns>どの停止経路でも次の出力を受け付けられる確認です。</returns>
    // 抑制理由: finallyで取消と書込みの完了確認を行った後にStreamとTaskを解放します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2025", Justification = "停止を発生させてからfinallyで書込みTaskの完了を待ちます。")]
    [Theory]
    [InlineData("disconnect", ELogExportFormat.Zip)]
    [InlineData("cancel", ELogExportFormat.Zip)]
    [InlineData("shutdown", ELogExportFormat.Zip)]
    [InlineData("disconnect", ELogExportFormat.Log)]
    [InlineData("cancel", ELogExportFormat.Log)]
    [InlineData("shutdown", ELogExportFormat.Log)]
    public async Task CancellationReleasesInFlightSearchAsync(string reason, ELogExportFormat format)
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource disconnected = new();
        using LogExportJobs jobs = new(Clusters, async (_, _, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            return null;
        }, NamesAsync);
        await jobs.StartAsync(CancellationToken.None);
        try
        {
            LogExportStatus running = jobs.Create(new(new()) { Format = format });
            using MemoryStream output = new();
            using Task writing = jobs.WriteAsync(running.Id, output, disconnected.Token);
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                switch (reason)
                {
                    case "disconnect":
                        await disconnected.CancelAsync();
                        break;
                    case "cancel":
                        await jobs.CancelAsync(running.Id);
                        break;
                    case "shutdown":
                        await jobs.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
                        break;
                    default:
                        throw new InvalidOperationException("Unknown test case.");
                }
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing.WaitAsync(TimeSpan.FromSeconds(10)));
            }
            finally
            {
                // 後始末の取消で停止通知の欠落を隠さないよう、結果の検証は取消前に済ませます。
                await disconnected.CancelAsync();
                await writing.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            }
            string shutdown = "shutdown";
            if (!string.Equals(reason, shutdown, StringComparison.Ordinal))
            {
                Assert.Equal("cancelled", jobs.Read(running.Id)!.State);
                LogExportStatus next = jobs.Create(new(new()));
                await jobs.CancelAsync(next.Id);
            }
        }
        finally
        {
            await jobs.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>本番へ接続しない一つの観測先です。</summary>
    /// <returns>隔離した識別子です。</returns>
    private static IReadOnlyList<DiscoveredCluster> Clusters() => [new("cluster", [new("peer", Guid.NewGuid(), "host", "127.0.0.1", 1234, EPeerStatus.Ready, 1, null, DateTimeOffset.UtcNow)], [], DateTimeOffset.UtcNow)];

    /// <summary>WFに属さないログの試験で、不要な本文取得を行いません。</summary>
    /// <returns>空の名前一覧です。</returns>
    private static Task<LogNames> NamesAsync(FileTransferPeer peer, LogNamesQuery query, CancellationToken cancellationToken)
        => Task.FromResult(new LogNames(null, null, new Dictionary<string, string>(StringComparer.Ordinal)));

    /// <summary>長時間待機せず保持期限を進めます。</summary>
    private sealed class ExportClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;

        /// <summary>現在の論理時刻です。</summary>
        /// <returns>試験が進めた時刻です。</returns>
        public override DateTimeOffset GetUtcNow() => now;

        /// <summary>保持時間だけを進めます。</summary>
        internal void Advance(TimeSpan amount) => now += amount;
    }
}
