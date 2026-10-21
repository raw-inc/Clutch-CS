using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>独立して担当を持った実二Coreの再会と、その後の安定維持を検証します。</summary>
public sealed class CoordinatorContentTests
{
    /// <summary>各側単独の担当が再会して一致し、停止後に戻る同順位Peerへ不要に復帰しません。</summary>
    /// <returns>実プロセスと実UDP経由の選出結果です。</returns>
    [Fact]
    public async Task IndependentlyReadyPeersConvergeAndRestartedOwnerDoesNotPreempt()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true);
        CorePeer second = new(cluster, "b", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await first.StartAsync();
        await WaitOwnerAsync(first, "a", 1).ConfigureAwait(true);
        await WaitEffectiveAsync(first).ConfigureAwait(true);
        await second.StartAsync();
        await WaitOwnerAsync(second, "a", 2).ConfigureAwait(true);
        await WaitEffectiveAsync(second).ConfigureAwait(true);
        await second.StopAsync();
        await WaitOwnerAsync(first, "a", 1).ConfigureAwait(true);
        await first.StopAsync();
        await second.StartAsync();
        await WaitOwnerAsync(second, "b", 1).ConfigureAwait(true);
        await second.StopAsync();
        await first.StartAsync();
        await WaitOwnerAsync(first, "a", 1).ConfigureAwait(true);
        await second.StartAsync();
        string? owner = null;
        string? lastStatus = null;
        try
        {
            await CorePeer.WaitAsync(async token =>
            {
                JsonElement a = await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
                JsonElement b = await second.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
                lastStatus = JsonSerializer.Serialize(new { a, b });
                owner = a.GetProperty("coordinator").GetString();
                return owner is not null && string.Equals(owner, b.GetProperty("coordinator").GetString(), StringComparison.Ordinal) && ReadyCount(a) == 2 && ReadyCount(b) == 2;
            }).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("Coordinator convergence was not confirmed: " + lastStatus);
        }
        for (int index = 0; index < 6; index++)
        {
            await Task.Delay(1000).ConfigureAwait(true);
            Assert.Equal(owner, (await first.GetAsync<JsonElement>("status")).GetProperty("coordinator").GetString());
            Assert.Equal(owner, (await second.GetAsync<JsonElement>("status")).GetProperty("coordinator").GetString());
        }
        CorePeer stopped = first;
        CorePeer surviving = second;
        string survivingId = "b";
        string secondId = "b";
        if (string.Equals(owner, secondId, StringComparison.Ordinal))
        {
            stopped = second;
            surviving = first;
            survivingId = "a";
        }
        await stopped.StopAsync();
        await WaitOwnerAsync(surviving, survivingId, 1).ConfigureAwait(true);
        await stopped.StartAsync();
        await WaitOwnerAsync(surviving, survivingId, 2).ConfigureAwait(true);
        await WaitOwnerAsync(stopped, survivingId, 2).ConfigureAwait(true);
    }

    /// <summary>確認されたReady実体だけを数えます。</summary>
    /// <returns>状態通知の通常参加数です。</returns>
    private static int ReadyCount(JsonElement status) => status.GetProperty("peers").EnumerateArray().Count(peer => string.Equals(peer.GetProperty("status").GetString(), "ready", StringComparison.Ordinal));

    /// <summary>初回の暫定Readyだけでseed操作の途中を強制終了しません。</summary>
    /// <returns>確認した共通有効版の成立です。</returns>
    private static Task WaitEffectiveAsync(CorePeer peer) => CorePeer.WaitAsync(async token =>
        (await peer.GetAsync<ClusterOperationalState>("configuration.read", token).ConfigureAwait(true)).Effective is not null);

    /// <summary>指定担当と参加集合の両方が成立するまで待ちます。</summary>
    /// <returns>実管理照会による成立確認です。</returns>
    private static Task WaitOwnerAsync(CorePeer peer, string owner, int count) => CorePeer.WaitAsync(async token =>
    {
        JsonElement status = await peer.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
        return string.Equals(status.GetProperty("coordinator").GetString(), owner, StringComparison.Ordinal) && ReadyCount(status) == count;
    });
}
