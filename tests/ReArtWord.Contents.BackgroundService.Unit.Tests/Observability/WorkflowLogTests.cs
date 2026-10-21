using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Logging;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Observability;

/// <summary>ログの秘密保護、保持段階、失敗復旧を実体で検証します。</summary>
public sealed class WorkflowLogTests
{
    /// <summary>秘密照合ができない場合は生値を出さず欠落として返します。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task UnavailableSecretFilteringNeverWritesRawContentAsync()
    {
        using LogTestEnvironment fixture = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await fixture.Secrets.SetAsync("api", "never-expose-987", timeout.Token).ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "repository.json"), "corrupt", timeout.Token).ConfigureAwait(true);
        WorkflowLogService log = new(new("peer", fixture.Logs), fixture.Repository, fixture.Secrets);
        await using ConfiguredAsyncDisposable lifetime = log.ConfigureAwait(true);
        await log.StartAsync(timeout.Token).ConfigureAwait(true);
        await log.WriteAsync(new(Guid.NewGuid(), DateTimeOffset.UtcNow, "ERROR", "peer", "never-expose-987"), timeout.Token).ConfigureAwait(true);
        Assert.Equal(1, log.Statistics.DroppedEntries);
        Assert.Equal(0, log.Statistics.PersistedEntries);
        Assert.False((await log.QueryAsync(new(), timeout.Token).ConfigureAwait(true)).Complete);
    }

    /// <summary>全レベルで同じローリング基準時刻を使用します。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task RollingTimeIsSharedAcrossLevelsAndPreservesEventTimeAsync()
    {
        using LogTestEnvironment fixture = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        LogTestClock clock = new();
        WorkflowLogService log = new(new("peer", fixture.Logs, RollingTime: "12:00"), fixture.Repository, fixture.Secrets, clock);
        await using ConfiguredAsyncDisposable lifetime = log.ConfigureAwait(true);
        await log.StartAsync(timeout.Token).ConfigureAwait(true);
        DateTimeOffset firstTime = clock.GetUtcNow();
        await log.WriteAsync(new(Guid.NewGuid(), firstTime, "INFO", "peer", "first"), timeout.Token).ConfigureAwait(true);
        await log.WriteAsync(new(Guid.NewGuid(), firstTime, "DEBUG", "peer", "second"), timeout.Token).ConfigureAwait(true);
        await log.FlushAsync(timeout.Token).ConfigureAwait(true);
        Assert.True(Directory.Exists(Path.Combine(fixture.Logs, "INFO", "2026-09-09")));
        Assert.True(Directory.Exists(Path.Combine(fixture.Logs, "DEBUG", "2026-09-09")));
        clock.Advance(TimeSpan.FromHours(2));
        await log.WriteAsync(new(Guid.NewGuid(), clock.GetUtcNow(), "INFO", "peer", "noon"), timeout.Token).ConfigureAwait(true);
        await log.FlushAsync(timeout.Token).ConfigureAwait(true);
        Assert.True(Directory.Exists(Path.Combine(fixture.Logs, "INFO", "2026-09-10")));
        Assert.Equal(2, (await log.QueryAsync(new(Until: firstTime), timeout.Token).ConfigureAwait(true)).Entries.Count);
    }

    /// <summary>秘密値はDEBUG/binaryも伏せ、個人情報はDEBUGだけに残します。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task SecretsAreRedactedBeforeStorageAndAfterRotationAsync()
    {
        using LogTestEnvironment fixture = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        await fixture.Secrets.SetAsync("api", "never-log-123", timeout.Token).ConfigureAwait(true);
        WorkflowLogService log = new(new("peer", fixture.Logs), fixture.Repository, fixture.Secrets);
        await using ConfiguredAsyncDisposable lifetime = log.ConfigureAwait(true);
        await log.StartAsync(timeout.Token).ConfigureAwait(true);
        WorkflowLogEntry info = new(Guid.NewGuid(), DateTimeOffset.UtcNow, "INFO", "peer", "person-name never-log-123", Data: NodeValue.FromObject(new { name = "person-name" }), ContainsPersonalData: true);
        WorkflowLogEntry debug = info with { Id = Guid.NewGuid(), Level = "DEBUG", Data = NodeValue.FromObject(new { name = "person-name", credential = "never-log-123", password = "unregistered-password" }) };
        await log.WriteAsync(info, timeout.Token).ConfigureAwait(true);
        await log.WriteAsync(debug, timeout.Token).ConfigureAwait(true);
        await log.WriteAsync(debug with { Id = Guid.NewGuid(), Data = NodeValue.FromBytes(Encoding.UTF8.GetBytes("head never-log-123 tail")) }, timeout.Token).ConfigureAwait(true);
        await fixture.Secrets.SetAsync("api", "next-secret-456", timeout.Token).ConfigureAwait(true);
        await log.WriteAsync(debug with { Id = Guid.NewGuid(), Message = "never-log-123 next-secret-456" }, timeout.Token).ConfigureAwait(true);
        await log.FlushAsync(timeout.Token).ConfigureAwait(true);
        LogQueryResult result = await log.QueryAsync(new(), timeout.Token).ConfigureAwait(true);
        Assert.True(result.Complete);
        Assert.Equal(4, result.Entries.Count);
        Assert.Null(result.Entries.Single(entry => entry.Id == info.Id).Data);
        Assert.Equal("person-name", result.Entries.Single(entry => entry.Id == debug.Id).Data!.Json.GetProperty("name").GetString());
        string serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("never-log-123", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("next-secret-456", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("unregistered-password", serialized, StringComparison.Ordinal);
        foreach (string path in Directory.GetFiles(fixture.Logs, "*.jsonl", SearchOption.AllDirectories))
        {
            string raw = await File.ReadAllTextAsync(path, timeout.Token).ConfigureAwait(true);
            Assert.DoesNotContain("never-log-123", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("next-secret-456", raw, StringComparison.Ordinal);
        }
    }

    /// <summary>日付単位zstd化、延長時の選択、新規限定短縮を確認します。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task RetentionCompressesExpandsAndHonorsExistingShorteningChoiceAsync()
    {
        using LogTestEnvironment fixture = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        LogTestClock clock = new();
        LogOptions options = new("peer", fixture.Logs, [new("INFO", 1, 5)]);
        WorkflowLogService log = new(options, fixture.Repository, fixture.Secrets, clock);
        await using ConfiguredAsyncDisposable lifetime = log.ConfigureAwait(true);
        await log.StartAsync(timeout.Token).ConfigureAwait(true);
        Guid id = Guid.NewGuid();
        await log.WriteAsync(new(id, clock.GetUtcNow(), "INFO", "peer", "retained", WorkflowId: "flow"), timeout.Token).ConfigureAwait(true);
        await log.FlushAsync(timeout.Token).ConfigureAwait(true);
        clock.Advance(TimeSpan.FromDays(2));
        await log.UpdateOptionsAsync(options, new(), timeout.Token).ConfigureAwait(true);
        Assert.NotEmpty(Directory.GetFiles(fixture.Logs, "*.zst", SearchOption.AllDirectories));
        Assert.Single((await log.QueryAsync(new(WorkflowId: "flow"), timeout.Token).ConfigureAwait(true)).Entries);
        options = options with { Retention = [new("INFO", 4, 10)] };
        await log.UpdateOptionsAsync(options, new(), timeout.Token).ConfigureAwait(true);
        Assert.NotEmpty(Directory.GetFiles(fixture.Logs, "*.zst", SearchOption.AllDirectories));
        await log.UpdateOptionsAsync(options, new(ExpandExistingCompressedOnRawExtension: true), timeout.Token).ConfigureAwait(true);
        Assert.Empty(Directory.GetFiles(fixture.Logs, "*.zst", SearchOption.AllDirectories));
        options = options with { Retention = [new("INFO", 0, 1)] };
        await log.UpdateOptionsAsync(options, new(), timeout.Token).ConfigureAwait(true);
        Assert.Single((await log.QueryAsync(new(WorkflowId: "flow"), timeout.Token).ConfigureAwait(true)).Entries);
        await log.UpdateOptionsAsync(options, new(ApplyDeletionShorteningToExisting: true), timeout.Token).ConfigureAwait(true);
        Assert.Empty((await log.QueryAsync(new(WorkflowId: "flow"), timeout.Token).ConfigureAwait(true)).Entries);
    }

    /// <summary>保存先が一時利用不能でも本来の呼出しは失敗せず元キューを回復します。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task StorageOutageKeepsBoundedQueueAndRecoversAsync()
    {
        using LogTestEnvironment fixture = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        await File.WriteAllTextAsync(fixture.Logs, "blocked", timeout.Token).ConfigureAwait(true);
        WorkflowLogService log = new(new("peer", fixture.Logs, QueueBytes: 4096), fixture.Repository, fixture.Secrets);
        await using ConfiguredAsyncDisposable lifetime = log.ConfigureAwait(true);
        await log.StartAsync(timeout.Token).ConfigureAwait(true);
        await log.WriteAsync(new(Guid.NewGuid(), DateTimeOffset.UtcNow, "ERROR", "peer", "still running"), timeout.Token).ConfigureAwait(true);
        Assert.NotEqual(0, log.Statistics.QueuedBytes);
        File.Delete(fixture.Logs);
        await log.FlushAsync(timeout.Token).ConfigureAwait(true);
        LogQueryResult result = await log.QueryAsync(new(), timeout.Token).ConfigureAwait(true);
        Assert.True(result.Complete);
        Assert.Single(result.Entries);
        Assert.Equal(0, log.Statistics.DroppedEntries);
    }

    /// <summary>バイト上限で拒否したログ数を明示し、呼出側へ例外を返しません。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task OversizeEntryIsReportedAsDroppedWithoutStoppingCallerAsync()
    {
        using LogTestEnvironment fixture = new();
        WorkflowLogService log = new(new("peer", fixture.Logs, QueueBytes: 1024), fixture.Repository, fixture.Secrets);
        await using ConfiguredAsyncDisposable lifetime = log.ConfigureAwait(true);
        await log.StartAsync().ConfigureAwait(true);
        await log.WriteAsync(new(Guid.NewGuid(), DateTimeOffset.UtcNow, "FATAL", "peer", new string('x', 5000))).ConfigureAwait(true);
        Assert.Equal(1, log.Statistics.DroppedEntries);
        Assert.True(5000 <= log.Statistics.DroppedBytes);
        Assert.Equal(0, log.Statistics.QueuedBytes);
    }

    /// <summary>一ファイルの圧縮不能で他ファイルの保持処理を止めず元未加工のを残します。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task CompressionFailurePreservesOriginalAndOtherSegmentsContinueAsync()
    {
        using LogTestEnvironment fixture = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        LogTestClock clock = new();
        LogOptions options = new("peer", fixture.Logs);
        WorkflowLogService log = new(options, fixture.Repository, fixture.Secrets, clock);
        await using ConfiguredAsyncDisposable lifetime = log.ConfigureAwait(true);
        await log.StartAsync(timeout.Token).ConfigureAwait(true);
        await log.WriteAsync(new(Guid.NewGuid(), clock.GetUtcNow(), "INFO", "peer", "first"), timeout.Token).ConfigureAwait(true);
        await log.FlushAsync(timeout.Token).ConfigureAwait(true);
        string firstFile = Directory.GetFiles(fixture.Logs, "*.jsonl", SearchOption.AllDirectories).Single();
        await log.WriteAsync(new(Guid.NewGuid(), clock.GetUtcNow(), "INFO", "peer", "second"), timeout.Token).ConfigureAwait(true);
        await log.FlushAsync(timeout.Token).ConfigureAwait(true);
        options = options with { Retention = [new("INFO", 0, null)] };
        using (FileStream locked = new(firstFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await log.UpdateOptionsAsync(options, new(), timeout.Token).ConfigureAwait(true);
            Assert.True(File.Exists(firstFile));
            Assert.NotEmpty(Directory.GetFiles(fixture.Logs, "*.zst", SearchOption.AllDirectories));
            Assert.False((await log.QueryAsync(new(), timeout.Token).ConfigureAwait(true)).Complete);
        }
        await log.UpdateOptionsAsync(options, new(), timeout.Token).ConfigureAwait(true);
        Assert.False(File.Exists(firstFile));
        Assert.True((await log.QueryAsync(new(), timeout.Token).ConfigureAwait(true)).Complete);
    }

    /// <summary>再接続・再作成後も因果IDで検索し、安定した続き位置で全件を取得します。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task SearchSurvivesRestartAndPaginatesAcrossPeersByCausationAsync()
    {
        using LogTestEnvironment fixture = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        Guid causation = Guid.NewGuid();
        LogOptions options = new("peer", fixture.Logs);
        WorkflowLogService original = new(options, fixture.Repository, fixture.Secrets);
        await using (original.ConfigureAwait(true))
        {
            await original.StartAsync(timeout.Token).ConfigureAwait(true);
            for (int index = 0; index < 5; index++)
            {
                string peer = "one";
                if (index % 2 == 0)
                {
                    peer = "two";
                }
                await original.WriteAsync(new(Guid.NewGuid(), DateTimeOffset.UtcNow, "DEBUG", peer, "record " + index, CausationId: causation), timeout.Token).ConfigureAwait(true);
            }
            await original.FlushAsync(timeout.Token).ConfigureAwait(true);
        }
        WorkflowLogService restarted = new(options, fixture.Repository, fixture.Secrets);
        await using ConfiguredAsyncDisposable lifetime = restarted.ConfigureAwait(true);
        await restarted.StartAsync(timeout.Token).ConfigureAwait(true);
        List<WorkflowLogEntry> all = [];
        string? cursor = null;
        do
        {
            LogQueryResult page = await restarted.QueryAsync(new(Limit: 2, Cursor: cursor, CausationId: causation), timeout.Token).ConfigureAwait(true);
            Assert.True(page.Complete);
            all.AddRange(page.Entries);
            cursor = page.Cursor;
        }
        while (cursor is not null);
        Assert.Equal(5, all.Count);
        Assert.Equal(5, all.Select(entry => entry.Id).Distinct().Count());
        Assert.Equal(3, (await restarted.QueryAsync(new(Peers: ["two"], CausationId: causation), timeout.Token).ConfigureAwait(true)).Entries.Count);
    }
}
