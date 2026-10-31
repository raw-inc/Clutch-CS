using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Testing.Performance;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests.Performance;

/// <summary>共通の大きい履歴を再書出しせず、小さい双方向差分だけで実Coreが再参加できることを確認します。</summary>
[Collection("Performance"), Trait("Category", "Performance")]
public sealed class DifferentialSynchronizationTests(ITestOutputHelper output)
{
    /// <summary>4MiB以上の履歴に対して転送上限を512KiBとし、差分選択前の全書出しを検出します。</summary>
    /// <returns>実UDP・実TCPの再参加、両側の値の一致とプロセスI/Oの測定です。</returns>
    [Fact]
    public async Task RejoinTransfersOnlyChangesWhenCommonHistoryExceedsSnapshotCapacityAsync()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        string? executable = Environment.GetEnvironmentVariable("CONTENTS_PROBE_CORE");
        CorePeer first = new(cluster, "a", port, true, executablePath: executable), second = new(cluster, "b", port, false, executablePath: executable);
        await using ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await ReadyAsync(first, second).ConfigureAwait(true);
        await first.StopAsync().ConfigureAwait(true);
        await second.StopAsync().ConfigureAwait(true);
        IDurableRepository left = await RepositoryFactory.CreateAsync(Path.Combine(first.DirectoryPath, "state"), "a", new()).ConfigureAwait(true);
        IDurableRepository right = await RepositoryFactory.CreateAsync(Path.Combine(second.DirectoryPath, "state"), "b", new()).ConfigureAwait(true);
        string? version = null;
        for (int index = 0; index < 64; index++)
        {
            NodeValue value = NodeValue.FromObject(index.ToString("D8", System.Globalization.CultureInfo.InvariantCulture) + new string('h', 65528));
            version = Assert.Single((await left.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:sync-cost", "history", version, value)]).ConfigureAwait(true)).Entries).Version;
        }
        Assert.True(await right.ImportAsync(await left.ExportAsync().ConfigureAwait(true)).ConfigureAwait(true));
        Assert.Equal(EOutcome.Succeeded, (await left.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:sync-cost", "left", null, NodeValue.FromObject(41))]).ConfigureAwait(true)).Outcome);
        Assert.Equal(EOutcome.Succeeded, (await right.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:sync-cost", "right", null, NodeValue.FromObject(42))]).ConfigureAwait(true)).Outcome);
        foreach (CorePeer peer in new[] { first, second })
        {
            string configuration = Path.Combine(peer.DirectoryPath, "contents.toml");
            string text = await File.ReadAllTextAsync(configuration).ConfigureAwait(true);
            await File.WriteAllTextAsync(configuration, text.Replace("[cluster]", "[cluster]\nmaximumSyncSnapshotBytes = 524288\nsyncSessionLifetimeSeconds = 30", StringComparison.Ordinal)).ConfigureAwait(true);
        }
        Stopwatch elapsed = Stopwatch.StartNew();
        await first.StartAsync().ConfigureAwait(true);

        // 同期元の復旧後に、差分を持つ相手を再参加させます。
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("health", token).ConfigureAwait(true)).GetProperty("ready").GetBoolean()).ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        int firstPid = (await first.GetAsync<JsonElement>("health").ConfigureAwait(true)).GetProperty("processId").GetInt32();
        int secondPid = (await second.GetAsync<JsonElement>("health").ConfigureAwait(true)).GetProperty("processId").GetInt32();
        ProcessResourceSample[] before = [ProcessResourceSample.Capture(firstPid), ProcessResourceSample.Capture(secondPid)];
        await ReadyAsync(first, second).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await left.ReadAsync("workflow:sync-cost", "right", token).ConfigureAwait(true))?.Value?.Json.GetInt32() == 42
            && (await right.ReadAsync("workflow:sync-cost", "left", token).ConfigureAwait(true))?.Value?.Json.GetInt32() == 41).ConfigureAwait(true);
        elapsed.Stop();
        ProcessResourceSample[] after = [ProcessResourceSample.Capture(firstPid), ProcessResourceSample.Capture(secondPid)];
        Assert.Equal(await left.GetSharedVersionAsync().ConfigureAwait(true), await right.GetSharedVersionAsync().ConfigureAwait(true));
        string json = JsonSerializer.Serialize(new { historyBytes = 64L * 65536, maximumSnapshotBytes = 524288, milliseconds = elapsed.Elapsed.TotalMilliseconds,
            before, after, processWriteBytes = after.Sum(sample => sample.WriteBytes) - before.Sum(sample => sample.WriteBytes) });
        string measurement = Path.Combine(first.DirectoryPath, "differential-sync-measurement.json");
        await File.WriteAllTextAsync(measurement, json).ConfigureAwait(true);
        output.WriteLine(measurement);
        output.WriteLine(json);
    }

    /// <summary>状態名だけでなく同期診断の解消と、両端から見た全実体の参加を確認します。</summary>
    /// <returns>小さい転送容量で実同期を終えた状態です。</returns>
    private static async Task ReadyAsync(CorePeer first, CorePeer second, CancellationToken cancellationToken = default)
    {
        JsonElement[] states = new JsonElement[2];
        try
        {
            await CorePeer.WaitAsync(async token =>
            {
                states[0] = await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
                states[1] = await second.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
                return states.All(state => state.GetProperty("synchronizationProblem").ValueKind == JsonValueKind.Null
                    && state.GetProperty("peers").EnumerateArray().Count(peer => peer.GetProperty("status").GetString() is "ready") == 2);
            }, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("差分同期が転送上限内で完了しません: " + JsonSerializer.Serialize(states.Select(state =>
            {
                if (state.ValueKind == JsonValueKind.Undefined)
                {
                    return null;
                }
                return new
                {
                    sync = state.GetProperty("synchronizationProblem"), peers = state.GetProperty("peers").EnumerateArray().Select(peer => new { id = peer.GetProperty("peerId"), state = peer.GetProperty("status") }),
                };
            })));
        }
    }
}
