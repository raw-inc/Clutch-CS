using System.Runtime.InteropServices;
using ReArtWord.Contents.BackgroundService.ClientHost.Updates.Fleet;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Fleet;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Management;

/// <summary>重い配布要求を繰り返し開かず、外部保存も表示へ反映します。</summary>
public sealed class FleetUpdateDisplayStoreTests
{
    /// <summary>変更なしではロック中もキャッシュを返し、同じIDの次の保存は再読込みします。</summary>
    /// <returns>実ファイルでのキャッシュ確認です。</returns>
    [Fact]
    public async Task UnchangedProgressDoesNotReopenTheManifestAndExternalWritesInvalidateItAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "contents-fleet-display-" + Guid.NewGuid().ToString("N"));
        try
        {
            FleetUpdateStore writer = new(directory), reader = new(directory);
            FleetUpdateTarget target = new("cluster", new("peer", Guid.NewGuid(), "host", "127.0.0.1", 1234, EPeerStatus.Ready, 0, null, DateTimeOffset.UtcNow),
                new("loopback", "127.0.0.1", "127.255.255.255", 1234));
            ReleaseManifest manifest = new("new", RuntimeInformation.RuntimeIdentifier, "app", [new("app", 0, new string('0', 64))], ["old"], "new");
            FleetUpdateRequest request = new(Guid.NewGuid(), new(manifest, directory, true), [target]);
            FleetUpdateStatus initial = new(request, false, EOutcome.Unknown, [new(target, "Queued", [])], DateTimeOffset.UtcNow);
            await writer.SaveAsync(initial).ConfigureAwait(true);
            FleetUpdateView snapshot = Assert.Single(await reader.ListViewsAsync().ConfigureAwait(true));
            string path = Path.Combine(directory, "fleet-updates", request.OperationId.ToString("N") + ".json");
            using (FileStream locked = new(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.Same(snapshot, Assert.Single(await reader.ListViewsAsync().ConfigureAwait(true)));
            }
            await writer.SaveAsync(initial with { Completed = true, Outcome = EOutcome.Succeeded, Peers = [new(target, "Completed", [])], UpdatedAt = initial.UpdatedAt.AddSeconds(1) }).ConfigureAwait(true);
            FleetUpdateView latest = Assert.Single(await reader.ListViewsAsync().ConfigureAwait(true));
            Assert.True(latest.Completed);
            Assert.Equal("Completed", Assert.Single(latest.Peers).State);
            Assert.NotSame(snapshot, latest);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
