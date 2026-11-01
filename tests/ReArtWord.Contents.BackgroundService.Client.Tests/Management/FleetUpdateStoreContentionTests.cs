using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Fleet;
using System.Runtime.InteropServices;
using ReArtWord.Contents.BackgroundService.ClientHost.Updates.Fleet;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Fleet;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Tests.Shared;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Management;

/// <summary>進捗ファイルの実アクセス拒否と、更新命令の一回性を併せて検証します。</summary>
public sealed class FleetUpdateStoreContentionTests
{
    /// <summary>準備後の進捗保存が競合しても、保存完了を待って同じ操作を一度だけ配送します。</summary>
    /// <returns>今回の更新ワーカー停止条件と副作用境界の回帰確認です。</returns>
    [WindowsFact]
    public async Task ContendedProgressWaitsBeforeDispatchingTheOriginalOperationAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "contents-fleet-contention-" + Guid.NewGuid().ToString("N"));
        FleetUpdateStore store = new(directory);
        FleetUpdateTarget target = new("cluster", new("peer", Guid.NewGuid(), "host", "127.0.0.1", 1234, EPeerStatus.Ready, 0, null, DateTimeOffset.UtcNow),
            new("loopback", "127.0.0.1", "127.255.255.255", 1234));
        ReleaseManifest manifest = new("new", RuntimeInformation.RuntimeIdentifier, "app", [new("app", 0, new string('0', 64))], ["old"], "new");
        FleetUpdateRequest request = new(Guid.NewGuid(), new(manifest, directory, true), [target]);
        FleetUpdateStatus initial = new(request, false, EOutcome.Unknown, [new(target, "Queued", [])], DateTimeOffset.UtcNow);
        FleetUpdateTestRemote remote = new([target]);
        string path = Path.Combine(directory, "fleet-updates", request.OperationId.ToString("N") + ".json");
        Guid? observedOperation = null;
        string updating = "Updating";
        try
        {
            FleetUpdateStatus result = await new FleetUpdateRunner(remote).RunAsync(initial, async (status, token) =>
            {
                if (!string.Equals(status.Peers[0].State, updating, StringComparison.Ordinal) || observedOperation.HasValue)
                {
                    await store.SaveAsync(status, token).ConfigureAwait(true);
                    return;
                }
                observedOperation = Assert.Single(status.Peers[0].Attempts).OperationId;
                File.SetAttributes(path, FileAttributes.ReadOnly);
                Task saving = store.SaveAsync(status, token);
                try
                {
                    await Task.Delay(150, token).ConfigureAwait(true);
                    Assert.False(saving.IsCompleted);
                    Assert.Equal("Preparing", (await store.ReadAsync(request.OperationId, token).ConfigureAwait(true))!.Peers[0].State);
                    Assert.Equal(["stage:host/cluster"], remote.Events.ToArray());
                }
                finally
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    await saving.ConfigureAwait(true);
                }
            }).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, result.Outcome);
            Assert.Equal(observedOperation, Assert.Single(result.Peers[0].Attempts).OperationId);
            Assert.Equal(["stage:host/cluster", "dispatch:host/cluster", "release:host/cluster"], remote.Events.ToArray());
            Assert.Equal(EOutcome.Succeeded, (await store.ReadAsync(request.OperationId).ConfigureAwait(true))!.Outcome);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
