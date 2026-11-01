using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Retention;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Testing.Performance;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>500msタイマーから4つのOSC送信までを、実Coreと実UDPで計測します。</summary>
[Collection("Performance"), Trait("Category", "Performance")]
public sealed class PeriodicHeartbeatProbeTests(ITestOutputHelper output)
{
    /// <summary>通常の共有保存を維持し、各回の送信値を混ぜずに有限回実行します。</summary>
    /// <returns>要求ごとの4出力と実到着間隔です。</returns>
    [Fact]
    public Task PeriodicSignalsReachAllOutputsWithoutDuplicationAsync() => RunAsync(3, 6, 0);

    /// <summary>診断保存周期より短い連続信号でも、全出力の値と回数を維持します。</summary>
    /// <returns>50ms周期・64実行・256 UDP出力の照合です。</returns>
    [Fact]
    public Task FrequentSignalsReachAllOutputsWithoutDuplicationAsync() => RunAsync(3, 64, 0, 50);

    /// <summary>7実Coreで大きい共有状態と500ms周期を継続し、通常運転中の再参加を検出します。</summary>
    /// <returns>30秒以上の送信、値の分離、参加状態の維持確認です。</returns>
    [Fact]
    public Task SevenPeersRemainReadyDuringSustainedSchedulingAsync() => RunAsync(7, 64, 8 * 1024 * 1024);

    /// <summary>指定した実Core数と有限回数で同じ通信ワークフローを検証します。</summary>
    /// <returns>全受信と参加状態の計測です。</returns>
    // 抑制理由: receivingはfinallyで取消後にawaitし、receiverとcaptureStopを先に破棄しません。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2025", Justification = "UDP受信タスクをfinallyで必ず回収してから所有資源を破棄します。")]
    private async Task RunAsync(int peerCount, int occurrences, int sharedBytes, int intervalMilliseconds = 500, CancellationToken cancellationToken = default)
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        string? executable = Environment.GetEnvironmentVariable("CONTENTS_PROBE_CORE");
        CorePeer[] peers = Enumerable.Range(0, peerCount).Select(index =>
        {
            string name = ((char)('a' + index)).ToString();
            return new CorePeer(cluster, name, port, index == 0, "host-" + name, 900 - index * 10, executable);
        }).ToArray();
        PeerLifetime lifetime = new(peers);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable peerLifetime = lifetime.ConfigureAwait(true);
        CorePeer first = peers[0], second = peers[1];
        foreach (CorePeer peer in peers)
        {
            await peer.StartAsync(cancellationToken).ConfigureAwait(true);
        }
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("peers").EnumerateArray()
            .Count(peer => peer.GetProperty("status").GetString() is "ready") == peerCount, cancellationToken).ConfigureAwait(true);
        int historyRows = 0;
        string? historySetting = Environment.GetEnvironmentVariable("CONTENTS_PROBE_HISTORY_ROWS");
        if (int.TryParse(historySetting, out int configured) && configured is > 0 and <= 2500000)
        {
            historyRows = configured;
        }
        if (0 < historyRows || 0 < sharedBytes)
        {
            await Task.Delay(2500, cancellationToken).ConfigureAwait(true);
            foreach (CorePeer peer in peers)
            {
                await peer.StopAsync(cancellationToken).ConfigureAwait(true);
            }
            foreach (CorePeer peer in peers)
            {
                if (0 < historyRows)
                {
                    await SeedDeadlinesAsync(peer, historyRows).ConfigureAwait(true);
                }
                if (0 < sharedBytes)
                {
                    SqliteRepository repository = new(Path.Combine(peer.DirectoryPath, "state", "state.db"));
                    Infrastructure.Persistence.Documents.RepositorySnapshot snapshot = await repository.ExportAsync(cancellationToken).ConfigureAwait(true);
                    Assert.True(await repository.ImportAsync(snapshot with { Entries = [.. snapshot.Entries,
                        new("benchmark:shared", "payload", "fixed", NodeValue.FromObject(new string('x', sharedBytes)))] }, cancellationToken).ConfigureAwait(true));
                }
                await peer.StartAsync(cancellationToken).ConfigureAwait(true);
            }
            await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("peers").EnumerateArray()
                .Count(peer => peer.GetProperty("status").GetString() is "ready") == peerCount, cancellationToken).ConfigureAwait(true);
        }
        List<int> processIds = [];
        foreach (CorePeer peer in peers)
        {
            processIds.Add((await peer.GetAsync<JsonElement>("health", cancellationToken).ConfigureAwait(true)).GetProperty("processId").GetInt32());
        }
        List<ProcessResourceSample[]> samples = [processIds.Select(ProcessResourceSample.Capture).ToArray()];
        using UdpClient receiver = new(new IPEndPoint(IPAddress.Loopback, 0));
        TargetDefinition local = new(EExecutionScope.One, ["a"]), remote = new(EExecutionScope.One, ["b"]);
        JsonElement settings = JsonSerializer.SerializeToElement(new { });
        List<NodeDefinition> nodes = [
            new("timer", "scheduling.timer", JsonSerializer.SerializeToElement(new { mode = "interval", intervalMilliseconds, missed = "delay", count = occurrences }), Target: local, StartOnLoad: true),
            new("now", "scheduling.now", JsonSerializer.SerializeToElement(new { format = "HH:mm:ss.fffffff" }), Target: local),
        ];
        List<WireDefinition> wires = [new("timer", "start", "now", "trigger")];
        int sink = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        for (int index = 0; index < 4; index++)
        {
            string suffix = index.ToString(System.Globalization.CultureInfo.InvariantCulture), packet = "packet-" + suffix, send = "send-" + suffix;
            nodes.Add(new(packet, "osc.packet", settings, new Dictionary<string, NodeValue> { ["address"] = NodeValue.FromObject("/heartbeat/" + suffix) }, Target: local));
            nodes.Add(new(send, "osc.udp.send", JsonSerializer.SerializeToElement(new
            {
                interfaceId = second.InterfaceId,
                localAddress = "127.0.0.1",
                remoteAddress = "127.0.0.1",
                remotePort = sink,
            }), Target: remote));
            wires.Add(new("now", "value", packet, "input1"));
            wires.Add(new(packet, "value", send, "value"));
        }
        WorkflowDefinition definition = new("heartbeat", "Periodic forwarding", nodes, wires, Enabled: true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(180));
        Stopwatch watch = Stopwatch.StartNew();
        List<(double Milliseconds, string Address, string Value)> received = [];
        List<string> unstable = [];
        using CancellationTokenSource captureStop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        Task<List<(double Milliseconds, string Address, string Value)>> receiving = ReceiveAsync(receiver, watch, occurrences * 4, captureStop.Token);
        try
        {
            WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null), timeout.Token).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
            while (!receiving.IsCompleted)
            {
                _ = await Task.WhenAny(receiving, Task.Delay(250, timeout.Token)).ConfigureAwait(true);
                JsonElement state = await first.GetAsync<JsonElement>("status", timeout.Token).ConfigureAwait(true);
                samples.Add(processIds.Select(ProcessResourceSample.Capture).ToArray());
                unstable.AddRange(state.GetProperty("peers").EnumerateArray().Where(peer => peer.GetProperty("status").GetString() is not "ready")
                    .Select(peer => peer.GetProperty("peerId").GetString() + ":" + peer.GetProperty("status").GetString()));
            }
            received = await receiving.ConfigureAwait(true);
        }
        finally
        {
            await captureStop.CancelAsync().ConfigureAwait(true);
            try
            {
                await receiving.ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (captureStop.IsCancellationRequested)
            {
                // 保存や状態照会の失敗時も、この試験が所有するUDP待受を残しません。
            }
        }
        await CorePeer.WaitAsync(async token =>
        {
            JsonElement status = await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
            return status.GetProperty("sourceQueue").GetProperty("running").GetInt32() == 0
                && status.GetProperty("sources").EnumerateArray().Any(source => source.GetProperty("nodeId").GetString() is "timer"
                    && source.GetProperty("status").GetProperty("state").GetString() is "completed");
        }, timeout.Token).ConfigureAwait(true);
        Assert.Equal(0, receiver.Available);
        Assert.Equal(occurrences, received.Select(item => item.Value).Distinct(StringComparer.Ordinal).Count());
        foreach (IGrouping<string, (double Milliseconds, string Address, string Value)> group in received.GroupBy(item => item.Value, StringComparer.Ordinal))
        {
            Assert.Equal(4, group.Count());
            Assert.Equal(["/heartbeat/0", "/heartbeat/1", "/heartbeat/2", "/heartbeat/3"], group.Select(item => item.Address).Order(StringComparer.Ordinal));
        }
        double[] starts = received.GroupBy(item => item.Value, StringComparer.Ordinal).Select(group => group.Min(item => item.Milliseconds)).Order().ToArray();
        double[] intervals = starts.Skip(1).Select((at, index) => at - starts[index]).ToArray();
        object result = new
        {
            label = Environment.GetEnvironmentVariable("CONTENTS_PROBE_LABEL") ?? "current",
            historyRows,
            peerCount,
            intervalMilliseconds,
            sharedBytes,
            unstable,
            firstArrivalMs = starts[0],
            meanIntervalMs = intervals.Average(),
            maximumIntervalMs = intervals.Max(),
            totalMs = watch.Elapsed.TotalMilliseconds,
            packets = received.Count,
            independentReceive = true,
            samples,
            intervals,
        };
        string path = Path.Combine(first.DirectoryPath, "heartbeat-timing.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(result), cancellationToken).ConfigureAwait(true);
        output.WriteLine(JsonSerializer.Serialize(result));
        output.WriteLine(path);
        Assert.Empty(unstable);
    }

    /// <summary>状態照会の応答待ちを到着間隔へ混ぜず、各UDPを継続して読みます。</summary>
    /// <returns>受信時点の時計と、全パケットの識別値です。</returns>
    private static async Task<List<(double Milliseconds, string Address, string Value)>> ReceiveAsync(UdpClient receiver, Stopwatch watch, int expected, CancellationToken cancellationToken = default)
    {
        List<(double Milliseconds, string Address, string Value)> received = [];
        while (received.Count < expected)
        {
            UdpReceiveResult packet = await receiver.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            double at = watch.Elapsed.TotalMilliseconds;
            string[] parts = Encoding.UTF8.GetString(packet.Buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(3, parts.Length);
            Assert.Equal(",s", parts[1]);
            received.Add((at, parts[0], parts[2]));
        }
        return received;
    }

    /// <summary>有限個の試験専用Coreを終了まで所有します。</summary>
    private sealed class PeerLifetime(CorePeer[] peers) : IAsyncDisposable
    {
        /// <summary>すべての試験プロセスと監視を回収します。</summary>
        /// <returns>終了確認です。</returns>
        async ValueTask IAsyncDisposable.DisposeAsync()
        {
            await Task.WhenAll(peers.Select(static peer => ((IAsyncDisposable)peer).DisposeAsync().AsTask())).ConfigureAwait(false);
        }
    }

    /// <summary>この試験が停止したCoreだけに、未到期の期限索引を置いて起動時の移行も検証します。</summary>
    /// <returns>本番保存を使わない大規模履歴の準備です。</returns>
    private static async Task SeedDeadlinesAsync(CorePeer peer, int count)
    {
        string path = Path.Combine(peer.DirectoryPath, "state", "state.db");
        Assert.True(File.Exists(path));
        SqliteRepository repository = new(path);
        NodeValue value = (await repository.ReadAsync("peer:repository-history", "policy").ConfigureAwait(true))!.Value!;
        DataRetentionSettings policy = value.Json.Deserialize<DataRetentionSettings>()!;
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync().ConfigureAwait(true);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE numbers(value) AS (VALUES(1) UNION ALL SELECT value+1 FROM numbers WHERE value<$count)
            INSERT INTO retention_deadlines(kind,key,category,recorded_at,delete_at,policy)
            SELECT 'receipt','performance-'||printf('%032d',value),$category,$now,$later,$policy FROM numbers;
            DROP INDEX IF EXISTS retention_deadline_policy_due;
            """;
        command.Parameters.AddWithValue("$count", count);
        command.Parameters.AddWithValue("$category", (int)ERetentionCategory.SharedHistory);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$later", DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$policy", ContractJson.Hash(policy));
        await command.ExecuteNonQueryAsync().ConfigureAwait(true);
    }
}
