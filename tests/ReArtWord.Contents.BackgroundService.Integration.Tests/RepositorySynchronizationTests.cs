using System.Runtime.CompilerServices;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>大きい共有保存を持つ実Coreへ新規参加し、再起動後にも同期します。</summary>
public sealed class RepositorySynchronizationTests
{
    /// <summary>4MiBを超える共有データへ複数の実プロセスが同時参加し、再起動後も同期できます。</summary>
    /// <returns>新規・再起動双方の実UDP同期確認です。</returns>
    [Fact]
    public async Task LargeRepositoryJoinsAndRestartsWithoutDatagramLimitIncrease()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true);
        CorePeer second = new(cluster, "b", port, false);
        CorePeer third = new(cluster, "c", port, false);
        await using ConfiguredAsyncDisposable firstLifetime = ((IAsyncDisposable)first).ConfigureAwait(true);
        await using ConfiguredAsyncDisposable secondLifetime = ((IAsyncDisposable)second).ConfigureAwait(true);
        await using ConfiguredAsyncDisposable thirdLifetime = ((IAsyncDisposable)third).ConfigureAwait(true);
        CorePeer[] peers = [first, second, third];
        await first.StartAsync().ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("health", token).ConfigureAwait(true)).GetProperty("ready").GetBoolean()).ConfigureAwait(true);
        for (int index = 0; index < 20; index++)
        {
            WorkflowDefinition definition = new("large-sync-" + index, "synchronization fixture", [new("value", "control.pass", JsonSerializer.SerializeToElement(new { }),
                new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(new string('x', 120000)) })], []);
            WorkflowSaveResult saved = await SaveAsync(first, new WorkflowSaveRequest(Guid.NewGuid(), definition, null)).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, saved.Shared.Outcome);
            Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        }
        Assert.True(4 * 1024 * 1024 < 20L * 120000 * 2);
        await Task.WhenAll(second.StartAsync(), third.StartAsync()).ConfigureAwait(true);
        await ReadyAsync(peers).ConfigureAwait(true);
        await VerifyAsync(second).ConfigureAwait(true);
        await VerifyAsync(third).ConfigureAwait(true);
        Guid previous = (await second.GetAsync<JsonElement>("health").ConfigureAwait(true)).GetProperty("instanceId").GetGuid();
        await second.StopAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await ReadyAsync(peers).ConfigureAwait(true);
        Assert.NotEqual(previous, (await second.GetAsync<JsonElement>("health").ConfigureAwait(true)).GetProperty("instanceId").GetGuid());
        await VerifyAsync(second).ConfigureAwait(true);
    }

    /// <summary>配送待ちを超えても同じ保存操作の結果だけを照会します。</summary>
    /// <returns>再実行せず確認した保存・反映結果です。</returns>
    private static async Task<WorkflowSaveResult> SaveAsync(CorePeer peer, WorkflowSaveRequest request, CancellationToken cancellationToken = default)
    {
        WorkflowSaveResult? result = null;
        try
        {
            result = await peer.PostAsync<WorkflowSaveResult>("workflows.save", request, cancellationToken).ConfigureAwait(true);
        }
        catch (InvalidOperationException)
        {
            // 元の操作IDを保持し、次の書込み要求は作りません。
        }
        await CorePeer.WaitAsync(async token =>
        {
            try
            {
                JsonElement response = await peer.GetAsync<JsonElement>("workflows.operations", request.OperationId.ToString("D"), token).ConfigureAwait(true);
                if (response.ValueKind == JsonValueKind.Null)
                {
                    return false;
                }
                result = response.Deserialize<WorkflowSaveResult>(ContractJson.Options)!;
                return result.Activation.Outcome == EOutcome.Succeeded || result.Saved.Outcome == EOutcome.Failed || result.Shared.Outcome == EOutcome.Failed || result.Snapshot.Outcome == EOutcome.Failed;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }, cancellationToken).ConfigureAwait(true);
        return result!;
    }

    /// <summary>軽量一覧と単体取得を実管理UDPで確認します。</summary>
    /// <returns>全定義の保存内容確認です。</returns>
    private static async Task VerifyAsync(CorePeer peer, CancellationToken cancellationToken = default)
    {
        WorkflowListPage list = await peer.GetAsync<WorkflowListPage>("workflows.list", new WorkflowListQuery(100), cancellationToken).ConfigureAwait(true);
        Assert.Equal(20, list.Entries.Count);
        Assert.Null(list.Cursor);
        JsonElement value = await peer.GetAsync<JsonElement>("workflows.read", "large-sync-19", cancellationToken).ConfigureAwait(true);
        Assert.Equal(120000, value.GetProperty("saved").GetProperty("definition").GetProperty("nodes")[0].GetProperty("inputs").GetProperty("value").GetProperty("json").GetString()!.Length);
        Assert.Equal(JsonValueKind.Null, (await peer.GetAsync<JsonElement>("health", cancellationToken).ConfigureAwait(true)).GetProperty("synchronizationProblem").ValueKind);
    }

    /// <summary>すべての実体が同じ参加集合を確認するまで待ちます。</summary>
    /// <returns>通常参加条件の確認です。</returns>
    private static async Task ReadyAsync(CorePeer[] peers, CancellationToken cancellationToken = default)
    {
        JsonElement[] states = new JsonElement[peers.Length];

        // 同期の再試行間隔は最大30秒です。大容量転送と再確認も同じ有限期限に含めます。
        try
        {
            await CorePeer.WaitAsync(async token =>
            {
                for (int index = 0; index < peers.Length; index++)
                {
                    states[index] = await peers[index].GetAsync<JsonElement>("status", token).ConfigureAwait(true);
                }
                return states.All(state => state.GetProperty("synchronizationProblem").ValueKind == JsonValueKind.Null
                    && state.GetProperty("peers").EnumerateArray().Count(item => string.Equals(item.GetProperty("status").GetString(), "ready", StringComparison.Ordinal)) == peers.Length);
            }, TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("同期後の通常参加を確認できません: " + JsonSerializer.Serialize(states));
        }
    }
}
