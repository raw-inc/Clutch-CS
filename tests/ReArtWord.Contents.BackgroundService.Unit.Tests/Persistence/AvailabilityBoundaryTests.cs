using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.Availability;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Availability;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>容量不足や代替保存不能を無断採用へ変えません。</summary>
public sealed class AvailabilityBoundaryTests
{
    /// <summary>Detached移行時に未確定候補を黙って破棄せず、両枝の混合を保留します。</summary>
    /// <returns>候補保全と全クライアントメタデータ一致の検証です。</returns>
    [Fact]
    public async Task DetachedTransitionPreservesExistingUnconfirmedCandidate()
    {
        using MediaTestFiles files = new();
        SqliteRepository repository = new(Path.Combine(files.Root, "state.db"));
        AvailabilityOptions options = new(new Dictionary<EStoragePurpose, AvailabilityPolicy> { [EStoragePurpose.Runtime] = new(Offline: EAvailabilityOffline.LocalUnconfirmed) });
        using AvailabilityService service = new("a", options, repository, files.Root);
        await service.RememberConfirmedAsync(EStoragePurpose.Runtime, "state", NodeValue.FromObject(1), "v1", true).ConfigureAwait(true);
        AvailabilityWriteResult staged = await service.StageOfflineAsync(EStoragePurpose.Runtime, "state", NodeValue.FromObject(2), "v1").ConfigureAwait(true);
        AvailabilityValue blocked = await service.RememberLocalBranchAsync(EStoragePurpose.Runtime, "state", NodeValue.FromObject(3), "detached").ConfigureAwait(true);
        Assert.False(blocked.State.CanRead);
        Assert.False(blocked.State.CanWrite);
        Assert.Equal("Detached.AvailabilityCandidatePending", blocked.State.Reason);
        Assert.Equal(blocked.State, Assert.Single(service.GetStates()));
        Assert.Equal(staged.Current!.Revision, blocked.State.CandidateRevision);
        Assert.Equal(2, service.TryGetCandidate(EStoragePurpose.Runtime, "state")!.Value!.Json.GetInt32());
        Assert.False(service.ReadOffline(EStoragePurpose.Runtime, "state").State.CanRead);
        Assert.False((await service.StageOfflineAsync(EStoragePurpose.Runtime, "state", NodeValue.FromObject(4), staged.Current.Revision).ConfigureAwait(true)).Applied);
    }

    /// <summary>大きな正常採用後に以前の小さな未保存値を表示し続けません。</summary>
    /// <returns>本文未保持と確定メタデータの検証です。</returns>
    [Fact]
    public async Task ConfirmedLargeAdoptionClearsObsoleteUnsavedValue()
    {
        using MediaTestFiles files = new();
        SqliteRepository repository = new(Path.Combine(files.Root, "state.db"));
        AvailabilityOptions options = new(new Dictionary<EStoragePurpose, AvailabilityPolicy> { [EStoragePurpose.Configuration] = new(EAvailabilitySaveFailure.UseUnsaved) }, MaximumBytes: 2048);
        using AvailabilityService service = new("a", options, repository, files.Root);
        await service.RememberConfirmedAsync(EStoragePurpose.Configuration, "config", NodeValue.FromObject(1), "v1", true).ConfigureAwait(true);
        await service.HandleSaveFailureAsync(EStoragePurpose.Configuration, "config", NodeValue.FromObject(2), "v2", "v1", Guid.NewGuid(), true, static (_, _) => Task.FromResult(EOutcome.Succeeded)).ConfigureAwait(true);
        await service.RememberConfirmedAsync(EStoragePurpose.Configuration, "config", NodeValue.FromObject(new string('x', 4000)), "v3", true).ConfigureAwait(true);
        AvailabilityState state = Assert.Single(service.GetStates());
        Assert.False(state.IsUnsaved);
        Assert.Null(state.CandidateRevision);
        Assert.Equal("v3", state.EffectiveRevision);
        Assert.Equal("v3", state.RecoveryRevision);
        Assert.Equal("Availability.CacheCapacity", state.Reason);
        Assert.Null(service.ReadOffline(EStoragePurpose.Configuration, "config").Value);
    }

    /// <summary>選択したfallbackが書けない場合は別媒体や揮発候補へ勝手に切り替えません。</summary>
    /// <returns>未確認保存の採用抑止です。</returns>
    [Fact]
    public async Task FailedExplicitFallbackDoesNotAdoptOrChooseAnotherBackend()
    {
        using MediaTestFiles files = new();
        SqliteRepository repository = new(Path.Combine(files.Root, "state.db"));
        AvailabilityOptions options = new(new Dictionary<EStoragePurpose, AvailabilityPolicy> { [EStoragePurpose.Configuration] = new(EAvailabilitySaveFailure.UseUnsaved, FallbackBackend: EStorageBackend.Json) });
        using AvailabilityService service = new("a", options, repository, files.Root);
        await service.RememberConfirmedAsync(EStoragePurpose.Configuration, "config", NodeValue.FromObject(1), "v1", true).ConfigureAwait(true);
        string blocked = Path.Combine(files.Root, "availability-fallback", "state.json.staging");
        Directory.CreateDirectory(blocked);
        try
        {
            int applied = 0;
            Task<EOutcome> Apply(NodeValue value, CancellationToken token)
            {
                applied++;
                return Task.FromResult(EOutcome.Succeeded);
            }
            AvailabilityWriteResult result = await service.HandleSaveFailureAsync(EStoragePurpose.Configuration, "config", NodeValue.FromObject(2), "v2", "v1", Guid.NewGuid(), true, Apply).ConfigureAwait(true);
            Assert.Equal(EOutcome.Unknown, result.PersistenceOutcome);
            Assert.False(result.Applied);
            await service.ActivateCandidateAsync(EStoragePurpose.Configuration, "config", Apply).ConfigureAwait(true);
            Assert.Equal(0, applied);
            Assert.False(File.Exists(Path.Combine(files.Root, "availability-fallback", "state.db")));
        }
        finally
        {
            Directory.Delete(blocked);
        }
    }
}
