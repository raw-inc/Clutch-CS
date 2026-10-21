using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Tracing;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Tracing;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Secrets;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Observability;

/// <summary>一時追跡の寿命と生通信の秘密保護を確認します。</summary>
public sealed class TraceTests
{
    /// <summary>開始時の秘密を初めて生通信記録する前に更新しても、旧値を漏らしません。</summary>
    /// <returns>未加工のだけを有効にした接続の旧秘密保護を確認します。</returns>
    [Fact]
    public async Task FirstRawResponseAfterSecretRotationStillMasksTheResolvedOldValue()
    {
        using LogTestEnvironment fixture = new();
        string oldValue = "prior / token + value";
        await fixture.Secrets.SetAsync("token", oldValue).ConfigureAwait(true);
        Assert.Equal(oldValue, await fixture.Secrets.GetAsync("token").ConfigureAwait(true));
        await fixture.Secrets.SetAsync("token", "replacement-token").ConfigureAwait(true);
        PeerCluster cluster = Cluster(Options(), Repository());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable clusterLifetime = cluster.ConfigureAwait(true);
        await cluster.StartAsync().ConfigureAwait(true);
        RecordingLog log = new();
        TraceRawLogger raw = new(cluster, log, fixture.Secrets);
        NodeExecutionContext context = new(Guid.NewGuid(), Guid.NewGuid(), "workflow", "revision", "node", "one", JsonSerializer.SerializeToElement(new { }), new Dictionary<string, NodeValue>(StringComparer.Ordinal), TimeProvider.System)
        {
            TracePolicy = new TracePolicy(true, new TraceSelection(Inputs: false, Outputs: false, RawCommunication: true)),
        };
        string[] echoed = [oldValue, Uri.EscapeDataString(oldValue), Convert.ToBase64String(Encoding.UTF8.GetBytes(oldValue))];
        await raw.WriteRawAsync(context, "http", false, Encoding.UTF8.GetBytes(string.Join(" | ", echoed))).ConfigureAwait(true);
        WorkflowLogEntry entry = Assert.Single(log.Entries);
        string recorded = Encoding.UTF8.GetString(entry.Data!.Binary.Span);
        foreach (string value in echoed)
        {
            Assert.DoesNotContain(value, recorded, StringComparison.Ordinal);
        }
        Assert.Contains("[redacted]", recorded, StringComparison.Ordinal);
    }

    /// <summary>入力・出力JSON内のbase64化NodeValueも保存前に保護します。</summary>
    /// <returns>検索で取得できる保護済み内容の検証完了。</returns>
    [Fact]
    public async Task NestedBinaryNodeValuesCannotBypassSecretMasking()
    {
        using LogTestEnvironment fixture = new();
        string secret = "nested-secret";
        await fixture.Secrets.SetAsync("token", secret);
        WorkflowLogService log = new(new("peer", fixture.Logs), fixture.Repository, fixture.Secrets);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = log.ConfigureAwait(false);
        await log.StartAsync();
        foreach (Encoding encoding in new[] { Encoding.UTF8, Encoding.Unicode, Encoding.BigEndianUnicode })
        {
            NodeValue binary = NodeValue.FromBytes(encoding.GetBytes("before " + secret + " after"));
            await log.WriteAsync(new WorkflowLogEntry(Guid.NewGuid(), DateTimeOffset.UtcNow, "DEBUG", "peer", "nested", Data: NodeValue.FromObject(new { value = binary })));
        }
        await log.FlushAsync();
        LogQueryResult result = await log.QueryAsync(new LogQuery());
        Assert.Equal(3, result.Entries.Count);
        foreach (WorkflowLogEntry entry in result.Entries)
        {
            byte[] bytes = entry.Data!.Json.GetProperty("value").GetProperty("Binary").GetBytesFromBase64();
            Assert.Contains("[redacted]", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
            Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)));
            Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(secret)));
            Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.BigEndianUnicode.GetBytes(secret)));
        }
    }

    /// <summary>秘密参照が失敗した場合に生内容を別経路へ出しません。</summary>
    /// <returns>安全な記録省略の検証完了。</returns>
    [Fact]
    public async Task RawTraceIsOmittedWhenSecretStoreCannotBeRead()
    {
        using LogTestEnvironment fixture = new();
        await using PeerCluster cluster = Cluster(Options(), Repository());
        await cluster.StartAsync();
        RecordingLog log = new();
        TraceRawLogger raw = new(cluster, log, fixture.Secrets);
        NodeExecutionContext context = new(Guid.NewGuid(), Guid.NewGuid(), "workflow", "revision", "node", "one", JsonSerializer.SerializeToElement(new { }), new Dictionary<string, NodeValue>(StringComparer.Ordinal), TimeProvider.System)
        {
            TracePolicy = new TracePolicy(true),
        };
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "repository.json"), "corrupt");
        await raw.WriteRawAsync(context, "udp", false, Encoding.UTF8.GetBytes("do-not-expose"));
        Assert.Empty(log.Entries);
    }

    /// <summary>全停止要求はtrace解除を先行し、受付操作記録確定後だけHost停止へ進めます。</summary>
    /// <returns>停止受付と明示再開保留解除の検証完了。</returns>
    [Fact]
    public async Task ClusterStopClearsTemporaryTraceBeforeHostCallbackAndDoesNotReplay()
    {
        SqliteRepository repository = Repository();
        await using PeerCluster cluster = Cluster(Options(), repository);
        await cluster.StartAsync();
        using TraceService trace = new(cluster, repository);
        Assert.Equal(EOutcome.Succeeded, (await trace.ChangeAsync(new TraceChange(Guid.NewGuid(), "workflow", true, null))).Outcome);
        int stops = 0;
        ClusterStopService stop = new(cluster, repository, trace) { StopHost = () => stops++ };
        TraceClusterStopRequest request = new(Guid.NewGuid(), trace.GetStatus("workflow", new TracePolicy(false)).Version,
            [new TraceStopTarget(cluster.LocalPeerId, cluster.LocalInstanceId)]);
        TraceClusterStopResult result = await stop.RequestAsync(request);
        Assert.Equal(EOutcome.Succeeded, result.TraceClear);
        Assert.Equal(EOutcome.Succeeded, Assert.Single(result.Targets).Acceptance);
        Assert.Equal(0, stops);
        Assert.False(trace.GetPolicy("workflow", new TracePolicy(false)).Enabled);
        Assert.Equal(EOutcome.Succeeded, (await stop.RequestAsync(request)).Outcome);
        Assert.Equal(EOutcome.NotExecuted, (await trace.ChangeAsync(new TraceChange(Guid.NewGuid(), "workflow", true, trace.GetStatus("workflow", new TracePolicy(false)).Version))).Outcome);
        await stop.RefreshAsync();
        await stop.RefreshAsync();
        Assert.Equal(1, stops);
        TraceStopState closing = await trace.GetStopStateAsync();
        Assert.Equal(request.OperationId, closing.OperationId);
        Assert.Equal(EOutcome.Succeeded, (await trace.ReleaseStopAsync(new TraceStopReleaseRequest(Guid.NewGuid(), closing.Version!))).Outcome);
        Assert.Equal(1, stops);
        Assert.Equal(EOutcome.Succeeded, (await trace.ChangeAsync(new TraceChange(Guid.NewGuid(), "workflow", true, trace.GetStatus("workflow", new TracePolicy(false)).Version))).Outcome);
    }

    /// <summary>古い実体指定は追跡解除もHost停止も始めません。</summary>
    /// <returns>未変更の確認完了。</returns>
    [Fact]
    public async Task ClusterStopRejectsChangedInstanceBeforeClearingTrace()
    {
        SqliteRepository repository = Repository();
        await using PeerCluster cluster = Cluster(Options(), repository);
        await cluster.StartAsync();
        using TraceService trace = new(cluster, repository);
        await trace.ChangeAsync(new TraceChange(Guid.NewGuid(), "workflow", true, null));
        ClusterStopService stop = new(cluster, repository, trace);
        TraceClusterStopResult result = await stop.RequestAsync(new TraceClusterStopRequest(Guid.NewGuid(), trace.GetStatus("workflow", new TracePolicy(false)).Version,
            [new TraceStopTarget(cluster.LocalPeerId, Guid.NewGuid())]));
        Assert.Equal(EOutcome.NotExecuted, result.Outcome);
        Assert.True(trace.GetPolicy("workflow", new TracePolicy(false)).Enabled);
        Assert.Empty(await repository.ListAsync("trace:cluster-stop"));
    }

    /// <summary>一時操作はPeer再起動で継続し、人解除は保存通常方針へ戻ります。</summary>
    /// <returns>保存と実効値の検証完了。</returns>
    [Fact]
    public async Task TemporaryTraceSurvivesPeerRestartAndClearRestoresSavedPolicy()
    {
        SqliteRepository repository = Repository();
        ClusterOptions options = Options();
        await using (PeerCluster first = Cluster(options, repository))
        {
            await first.StartAsync();
            using TraceService trace = new(first, repository);
            Assert.Equal(EOutcome.NotExecuted, (await trace.ChangeAsync(new TraceChange(Guid.NewGuid(), "workflow", true, null, new TraceSelection(RawByteLimit: 0)))).Outcome);
            TraceChange change = new(Guid.NewGuid(), "workflow", true, null, new TraceSelection(false, true, false));
            Assert.Equal(EOutcome.Succeeded, (await trace.ChangeAsync(change)).Outcome);
            Assert.True(trace.GetPolicy("workflow", new TracePolicy(false)).Enabled);
            Assert.Equal(EOutcome.Succeeded, (await trace.ChangeAsync(change)).Outcome);
            Assert.Equal(EOutcome.NotExecuted, (await trace.ChangeAsync(change with { Enabled = false })).Outcome);
        }
        await using PeerCluster second = Cluster(options, repository);
        await second.StartAsync();
        using TraceService restarted = new(second, repository);
        await restarted.RefreshAsync();
        TraceStatus status = restarted.GetStatus("workflow", new TracePolicy(false));
        Assert.True(status.Temporary);
        Assert.False(status.Effective.Selection!.Inputs);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (second.CoordinatorId is null)
        {
            await Task.Delay(20, timeout.Token);
        }
        Assert.Equal(EOutcome.Succeeded, (await restarted.ChangeAsync(new TraceChange(Guid.NewGuid(), "workflow", false, status.Version))).Outcome);
        Assert.False(restarted.GetPolicy("workflow", new TracePolicy(false)).Enabled);
        Assert.True(restarted.GetPolicy("workflow", new TracePolicy(true)).Enabled);
    }

    /// <summary>UTF8/UTF16秘密を全体から伏せてから先頭範囲を切り出します。</summary>
    /// <returns>生ログの検証完了。</returns>
    [Fact]
    public async Task RawRedactionPrecedesTruncationAndDisconnectedPeersDoNotLogRaw()
    {
        SqliteRepository repository = Repository();
        await using PeerCluster cluster = Cluster(Options(), repository);
        await cluster.StartAsync();
        string secret = "secret-value";
        await repository.ApplyBatchAsync(Guid.NewGuid(), [new DurableMutation("secrets", "token", null, NodeValue.FromObject(secret))]);
        RecordingLog log = new();
        TraceRawLogger raw = new(cluster, log, new SecretStore(repository));
        NodeExecutionContext context = new(Guid.NewGuid(), Guid.NewGuid(), "workflow", "revision", "node", "one", JsonSerializer.SerializeToElement(new { }), new Dictionary<string, NodeValue>(StringComparer.Ordinal), TimeProvider.System)
        {
            TracePolicy = new TracePolicy(true, new TraceSelection(RawByteLimit: 8)),
        };
        foreach (Encoding encoding in new[] { Encoding.UTF8, Encoding.Unicode, Encoding.BigEndianUnicode })
        {
            byte[] body = encoding.GetBytes(secret + " tail");
            await raw.WriteRawAsync(context, "http", false, body);
            WorkflowLogEntry entry = log.Entries[^1];
            Assert.Contains("[redact", Encoding.UTF8.GetString(entry.Data!.Binary.Span), StringComparison.Ordinal);
            Assert.Equal(body.Length, entry.Raw!.OriginalLength);
            Assert.True(entry.Raw.Truncated);
        }
        int recorded = log.Entries.Count;
        await raw.WriteRawAsync(context with { TracePolicy = new TracePolicy(true, new TraceSelection(RawCommunication: false)) }, "tcp", true, Encoding.UTF8.GetBytes("ordinary"));
        Assert.Equal(recorded, log.Entries.Count);
        await cluster.StopAsync();
        await raw.WriteRawAsync(context, "udp", false, Encoding.UTF8.GetBytes(secret));
        Assert.Equal(recorded, log.Entries.Count);
    }

    /// <summary>独立保存先です。</summary>
    /// <returns>SQLite リポジトリです。</returns>
    private static SqliteRepository Repository() => new(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "state.db"));

    /// <summary>実通信するPeerです。</summary>
    /// <returns>明示Ready条件付きPeerです。</returns>
    private static PeerCluster Cluster(ClusterOptions options, IDurableRepository repository) => new(options, repository) { ReadinessCheck = token => ValueTask.FromResult(true) };

    /// <summary>独立したUDP設定を作ります。</summary>
    /// <returns>保存して再利用できる設定です。</returns>
    private static ClusterOptions Options()
    {
        using UdpClient socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        return new(Guid.NewGuid().ToString("N"), "one", "host", "127.0.0.1", "127.255.255.255", ((IPEndPoint)socket.Client.LocalEndPoint!).Port, 1, true, Path.GetTempPath()) { PeerTimeout = TimeSpan.FromMilliseconds(100), HeartbeatInterval = TimeSpan.FromMilliseconds(25) };
    }

    /// <summary>保護済み境界へ渡された値を記録します。</summary>
    private sealed class RecordingLog : IWorkflowLog
    {
        /// <summary>記録された項目です。</summary>
        public List<WorkflowLogEntry> Entries
        {
            get;
        } = [];

        /// <summary>受け取った保護済みログを保持します。</summary>
        /// <returns>受付完了です。</returns>
        public ValueTask WriteAsync(WorkflowLogEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return ValueTask.CompletedTask;
        }
    }
}
