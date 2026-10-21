using System.Net;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.Availability;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Availability;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Configuration;

/// <summary>共通設定の実保存不能とローカル未保存採用・復旧版を区別します。</summary>
public sealed class ClusterOperationalAvailabilityTests
{
    /// <summary>JSON設定保存が失敗してもSQLite操作保存を維持し、Bだけが未来の設定へ反映します。</summary>
    /// <returns>実保存先と再起動後復旧版の検証です。</returns>
    [Theory]
    [InlineData(EAvailabilitySaveFailure.KeepConfirmed)]
    [InlineData(EAvailabilitySaveFailure.UseUnsaved)]
    public async Task FailedSettingsWriteKeepsOldRecoveryAndExplicitUnsaved(EAvailabilitySaveFailure policy)
    {
        using MediaTestFiles files = new();
        PersistenceOptions persistence = new(Purposes: new Dictionary<EStoragePurpose, EStorageBackend> { [EStoragePurpose.Configuration] = EStorageBackend.Json });
        IDurableRepository repository = await RepositoryFactory.CreateAsync(files.Root, "a", persistence).ConfigureAwait(true);
        AvailabilityOptions options = new(new Dictionary<EStoragePurpose, AvailabilityPolicy> { [EStoragePurpose.Configuration] = new(policy) });
        using AvailabilityService service = new("a", options, repository, files.Root);
        using UdpClient reservation = new(new IPEndPoint(IPAddress.Any, 0));
        int port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
        reservation.Dispose();
        ClusterOptions clusterOptions = new(Guid.NewGuid().ToString("N"), "a", "a", "127.0.0.1", "127.255.255.255", port, 1, true, files.Root);
        PeerCluster cluster = new(clusterOptions, repository) { ReadinessCheck = _ => ValueTask.FromResult(true) };
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = cluster.ConfigureAwait(true);
        await cluster.StartAsync().ConfigureAwait(true);
        using ClusterConfigurationStore store = new(repository, cluster) { Availability = service };
        ClusterOperationalSettings initial = new("UTC", EFileConflictPolicy.Fail, EFileInternalFailurePolicy.Keep, new Dictionary<string, IReadOnlyList<string>>());
        Assert.Equal(EOutcome.Succeeded, (await store.InitializeAsync(initial, true).ConfigureAwait(true)).Outcome);
        string original = store.Capture()!.Revision;
        string blocked = Path.Combine(files.Root, "state.json.staging");
        Directory.CreateDirectory(blocked);
        try
        {
            ClusterOperationalSaveRequest request = new(Guid.NewGuid(), initial with { TimeZoneId = "Asia/Tokyo" }, original);
            ClusterOperationalSaveResult result = await store.SaveAsync(request).ConfigureAwait(true);
            Assert.NotEqual(EOutcome.Succeeded, result.Saved.Outcome);
            Assert.NotEqual(EOutcome.Succeeded, result.Shared.Outcome);
            Assert.Equal(original, (await store.GetAsync().ConfigureAwait(true)).Saved!.Revision);
            AvailabilityState state = Assert.Single(service.GetStates());
            Assert.Equal(original, state.RecoveryRevision);
            bool unsaved = policy == EAvailabilitySaveFailure.UseUnsaved;
            Assert.Equal(unsaved, state.IsUnsaved);
            Assert.Equal(unsaved, result.Activation.Outcome == EOutcome.Succeeded);
            if (unsaved)
            {
                Assert.Equal("Asia/Tokyo", store.Capture()!.Settings.TimeZoneId);
                await store.ReconcileAsync().ConfigureAwait(true);
                Assert.Equal("Asia/Tokyo", store.Capture()!.Settings.TimeZoneId);
            }
            else
            {
                Assert.Equal("UTC", store.Capture()!.Settings.TimeZoneId);
            }
            Guid runtimeOperation = Guid.NewGuid();
            Assert.Equal(EOutcome.Succeeded, (await repository.ApplyBatchAsync(runtimeOperation, [new("receipts:a", "test", null, NodeValue.FromObject(42))]).ConfigureAwait(true)).Outcome);
            Assert.NotNull(await repository.ReadReceiptAsync(runtimeOperation).ConfigureAwait(true));
            using AvailabilityService restartedAvailability = new("a", options, repository, files.Root);
            using ClusterConfigurationStore restarted = new(repository, cluster) { Availability = restartedAvailability };
            await restarted.InitializeAsync(initial, false).ConfigureAwait(true);
            Assert.Equal("UTC", restarted.Capture()!.Settings.TimeZoneId);
            Assert.False(Assert.Single(restartedAvailability.GetStates()).IsUnsaved);
        }
        finally
        {
            Directory.Delete(blocked);
        }
    }
}
