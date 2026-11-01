using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Health;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Local;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Updates;

/// <summary>更新直後に試験が失敗しても、入替後の試験プロセスを残しません。</summary>
public sealed class UpdateFixtureLifetimeTests
{
    /// <summary>稼働照会で追跡される前の新プロセスも、試験の終了時に回収します。</summary>
    /// <returns>元の失敗、プロセス終了、専用配置の削除の確認です。</returns>
    [Fact]
    public async Task FailedTestReclaimsReplacementBeforeItsNextHealthReadAsync()
    {
        ProcessIdentity? replacement = null;
        string? root = null;
        try
        {
            IOException failure = await Assert.ThrowsAsync<IOException>(async () =>
            {
                using UpdateTestScope scope = new();
                root = scope.Root;
                (ReleaseManifest Manifest, string Root) original = await scope.PackageAsync("v1", true).ConfigureAwait(true);
                (Guid Instance, ProcessIdentity Process) started = await scope.StartOriginalAsync(original.Manifest, original.Root).ConfigureAwait(true);
                (ReleaseManifest Manifest, string Root) update = await scope.PackageAsync("v2", true).ConfigureAwait(true);
                UpdatePeerResult result = await LocalUpdateExecutor.ExecuteAsync(scope.Job(update.Manifest, update.Root, started.Instance, started.Process)).ConfigureAwait(true);
                Assert.Equal(EOutcome.Succeeded, result.Outcome);
                UpdateObserver observer = new(UpdateObserver.Binding(scope.Options), scope.Options.ClusterId!);
                await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = observer.ConfigureAwait(true);
                PeerInfo peer = await observer.FindAsync("fixture-peer", result.InstanceId).ConfigureAwait(true);
                ManagementReply reply = await observer.SendAsync(peer.PeerId, peer.InstanceId, "health", null, true).ConfigureAwait(true);
                Assert.Equal(EOutcome.Succeeded, reply.Outcome);
                replacement = new(reply.Value!.Json.GetProperty("processId").GetInt32(), reply.Value.Json.GetProperty("processStartedAt").GetDateTimeOffset());
                throw new IOException("Injected test failure after replacement.");
            }).ConfigureAwait(true);
            Assert.Equal("Injected test failure after replacement.", failure.Message);
            Assert.NotNull(replacement);
            Assert.False(ProcessOs.IsRunning(replacement));
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            if (replacement is not null && ProcessOs.IsRunning(replacement) != false)
            {
                Assert.Equal(EOutcome.Succeeded, await ProcessOs.StopAsync(replacement, EProcessStopPolicy.Force, 0).ConfigureAwait(true));
            }
        }
    }
}
