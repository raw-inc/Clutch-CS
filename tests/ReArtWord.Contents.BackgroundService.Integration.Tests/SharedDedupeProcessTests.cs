using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>異なるCoreでワークフローを実行しても、同じ共有キーの有効期間を引き継ぐことを確認します。</summary>
public sealed class SharedDedupeProcessTests
{
    /// <summary>確保したCoreを停止し、残るCoreが同じ保存版の上限を確認した後に再取得します。</summary>
    /// <returns>実プロセス間の重複抑止と、担当喪失後の有限な回復を確認します。</returns>
    [Fact]
    public async Task RemainingCoreReusesKeyAfterProvingTheOriginalTtl()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true, priority: 900), second = new(cluster, "b", port, false, priority: 100);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
        await first.StartAsync(timeout.Token).ConfigureAwait(true);
        await second.StartAsync(timeout.Token).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("peers").EnumerateArray()
            .Count(peer => string.Equals(peer.GetProperty("status").GetString(), "ready", StringComparison.Ordinal)) == 2, timeout.Token).ConfigureAwait(true);
        TimeSpan ttl = TimeSpan.FromSeconds(4);
        Assert.Equal("success", (await ReserveAsync(first, ttl, timeout.Token).ConfigureAwait(true)).Route);
        Assert.Equal("duplicate", (await ReserveAsync(second, ttl, timeout.Token).ConfigureAwait(true)).Route);
        await first.StopAsync(timeout.Token).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => string.Equals((await second.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("coordinator").GetString(), "b", StringComparison.Ordinal), timeout.Token).ConfigureAwait(true);
        Assert.Equal("state.dedupe.unconfirmed", (await ReserveAsync(second, ttl, timeout.Token).ConfigureAwait(true)).ReasonCode);
        await Task.Delay(ttl, timeout.Token).ConfigureAwait(true);
        Assert.Equal("success", (await ReserveAsync(second, ttl, timeout.Token).ConfigureAwait(true)).Route);
    }

    /// <summary>対象Coreで通常のノード実行を行います。照会ごとに別のWF入力を使用します。</summary>
    /// <returns>既存キーとの重複または新しい確保の結果です。</returns>
    private static async Task<NodeResult> ReserveAsync(CorePeer peer, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        JsonElement state = await peer.GetAsync<JsonElement>("status", cancellationToken).ConfigureAwait(false);
        string peerId = state.GetProperty("peerId").GetString()!;
        Guid operation = Guid.NewGuid();
        NodeDefinition node = new("event", "state.dedupe", JsonSerializer.SerializeToElement(new { scope = "workflow", key = "event", ttlMilliseconds = ttl.TotalMilliseconds }),
            Target: new(EExecutionScope.One, [peerId]));
        RemoteExecutionRequest request = new(operation, operation, "dedupe", "rev", node, new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(42) },
            peerId, state.GetProperty("instanceId").GetGuid(), DateTimeOffset.UtcNow.AddSeconds(20));
        return await peer.PostAsync<NodeResult>("nodes.execute", request, cancellationToken).ConfigureAwait(false);
    }
}
