using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.ClusterSynchronization;

/// <summary>固定スナップショットの読取りを有限に重ね、遅い一断片で全転送を直列待機しません。</summary>
public sealed class SynchronizationWindowTests
{
    /// <summary>先頭返信が保留でも後続を取得し、順序が逆転した本文を正しく復元します。</summary>
    /// <returns>実UDPと永続Importによる転送結果です。</returns>
    [Fact]
    public async Task SlowFirstChunkDoesNotBlockTheOtherBoundedReadsAsync()
    {
        string text = new('x', 90000);
        RepositorySnapshot snapshot = new([new("workflow:window", "value", "current", NodeValue.FromObject(text))], []);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentDictionary<long, byte> requested = new();
        TimeSpan observationLimit = TimeSpan.FromSeconds(10);

        // 応答順を確認する間に、障害再現用の短い通信期限が先に切れないようにします。
        SyncRecoveryPeer peer = new(snapshot, requestTimeout: observationLimit)
        {
            BeforeRead = async (request, token) =>
            {
                requested.TryAdd(request.Offset, 0);
                if (request.Offset == 0)
                {
                    await release.Task.WaitAsync(token).ConfigureAwait(false);
                }
            },
        };
        await using ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(observationLimit);
        try
        {
            await peer.StartAsync(timeout.Token).ConfigureAwait(true);
            while (requested.Count < 4 && peer.Target.SynchronizationProblem is null)
            {
                await peer.AnnounceAsync("different", cancellationToken: timeout.Token).ConfigureAwait(true);
                await Task.Delay(20, timeout.Token).ConfigureAwait(true);
            }
            Assert.Null(peer.Target.SynchronizationProblem);
            Assert.Equal(new long[] { 0, 8192, 16384, 24576 }, requested.Keys.Order().ToArray());
            Assert.Null(await peer.Repository.ReadAsync("workflow:window", "value", timeout.Token).ConfigureAwait(true));
            release.SetResult();
            DurableEntry? imported;
            do
            {
                await peer.AnnounceAsync("different", cancellationToken: timeout.Token).ConfigureAwait(true);
                await Task.Delay(20, timeout.Token).ConfigureAwait(true);
                imported = await peer.Repository.ReadAsync("workflow:window", "value", timeout.Token).ConfigureAwait(true);
            }
            while (imported is null);
            Assert.Equal(text, imported.Value!.Json.GetString());
            Assert.Null(peer.Target.SynchronizationProblem);
            Assert.Equal(1, peer.Opens);
        }
        finally
        {
            release.TrySetResult();
        }
    }
}
