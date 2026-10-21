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

/// <summary>保存失敗・現在有効・復旧版を別々に確認します。</summary>
public sealed class AvailabilityServiceTests
{
    /// <summary>明示Bを採用しても保存失敗を成功へ変えず、次の保存だけでは旧activeを消しません。</summary>
    /// <returns>候補の採用と再起動後の旧版の検証です。</returns>
    [Fact]
    public async Task UnsavedEffectiveKeepsDurableAndRecoveryRevisionsSeparate()
    {
        using MediaTestFiles files = new();
        SqliteRepository journal = new(Path.Combine(files.Root, "journal.db"));
        AvailabilityOptions options = Options(new(EAvailabilitySaveFailure.UseUnsaved));
        using AvailabilityService service = new("a", options, journal, files.Root);
        await service.RememberConfirmedAsync(EStoragePurpose.Configuration, "config", NodeValue.FromObject(42), "r1", true).ConfigureAwait(true);
        int applied = 0;
        Task<EOutcome> Apply(NodeValue value, CancellationToken token)
        {
            Assert.Equal(43, value.Json.GetInt32());
            applied++;
            return Task.FromResult(EOutcome.Succeeded);
        }
        Guid operation = Guid.NewGuid();
        AvailabilityWriteResult result = await service.HandleSaveFailureAsync(EStoragePurpose.Configuration, "config", NodeValue.FromObject(43), "r2", "r1", operation, true, Apply).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, result.PersistenceOutcome);
        Assert.True(result.Applied);
        Assert.Equal("r1", result.Current!.State.SavedRevision);
        Assert.Equal("r1", result.Current.State.RecoveryRevision);
        Assert.Equal("r2", result.Current.State.EffectiveRevision);
        Assert.True(result.Current.State.IsUnsaved);
        await service.HandleSaveFailureAsync(EStoragePurpose.Configuration, "config", NodeValue.FromObject(43), "r2", "r1", operation, true, Apply).ConfigureAwait(true);
        Assert.Equal(1, applied);
        AvailabilityValue onlySaved = await service.RememberConfirmedAsync(EStoragePurpose.Configuration, "config", NodeValue.FromObject(44), "r3").ConfigureAwait(true);
        Assert.Equal("r3", onlySaved.State.SavedRevision);
        Assert.Equal("r2", onlySaved.State.EffectiveRevision);
        Assert.Equal("r1", onlySaved.State.RecoveryRevision);
        Assert.True(onlySaved.State.IsUnsaved);
        Assert.Equal(43, onlySaved.Value!.Json.GetInt32());
        Assert.Null(await journal.ReadReceiptAsync(operation));
        using AvailabilityService restarted = new("a", options, journal, files.Root);
        await restarted.RememberConfirmedAsync(EStoragePurpose.Configuration, "config", NodeValue.FromObject(42), "r1", true).ConfigureAwait(true);
        Assert.Null(restarted.TryGetCandidate(EStoragePurpose.Configuration, "config"));
        Assert.Equal("r1", restarted.TryGetCurrent(EStoragePurpose.Configuration, "config")!.State.RecoveryRevision);
    }

    /// <summary>既定Aと保存不明ではコールバックを呼ばず、採用不明も自動再実行しません。</summary>
    /// <returns>強い結果境界の検証です。</returns>
    [Fact]
    public async Task DefaultAndUnknownDoNotSilentlyAdoptOrRepeat()
    {
        using MediaTestFiles files = new();
        SqliteRepository journal = new(Path.Combine(files.Root, "journal.db"));
        using AvailabilityService defaults = new("a", new(), journal, files.Root);
        await defaults.RememberConfirmedAsync(EStoragePurpose.Configuration, "config", NodeValue.FromObject(1), "r1", true).ConfigureAwait(true);
        int calls = 0;
        Task<EOutcome> Apply(NodeValue value, CancellationToken token)
        {
            calls++;
            return Task.FromResult(EOutcome.Unknown);
        }
        Assert.False((await defaults.HandleSaveFailureAsync(EStoragePurpose.Configuration, "config", NodeValue.FromObject(2), "r2", "r1", Guid.NewGuid(), true, Apply).ConfigureAwait(true)).Applied);
        Assert.Equal(0, calls);
        using AvailabilityService configured = new("a", Options(new(EAvailabilitySaveFailure.UseUnsaved)), journal, files.Root);
        await configured.RememberConfirmedAsync(EStoragePurpose.Configuration, "config", NodeValue.FromObject(1), "r1", true).ConfigureAwait(true);
        Assert.False((await configured.HandleSaveFailureAsync(EStoragePurpose.Configuration, "config", NodeValue.FromObject(2), "r2", "r1", Guid.NewGuid(), false, Apply).ConfigureAwait(true)).Applied);
        Assert.Equal(0, calls);
        AvailabilityWriteResult uncertain = await configured.HandleSaveFailureAsync(EStoragePurpose.Configuration, "config", NodeValue.FromObject(2), "r2", "r1", Guid.NewGuid(), true, Apply).ConfigureAwait(true);
        Assert.Equal(EOutcome.Unknown, uncertain.AdoptionOutcome);
        Assert.Equal(EOutcome.Failed, uncertain.PersistenceOutcome);
        Assert.Equal(EOutcome.Unknown, (await configured.ActivateCandidateAsync(EStoragePurpose.Configuration, "config", Apply).ConfigureAwait(true)).AdoptionOutcome);
        Assert.Equal(1, calls);
    }

    /// <summary>明示した二媒体の代替候補だけを保存・復旧し、旧版との基準不一致では使いません。</summary>
    /// <param name="backend">代替として明示した実媒体です。</param>
    /// <returns>代替保存の再起動検証です。</returns>
    [Theory]
    [InlineData(EStorageBackend.Sqlite)]
    [InlineData(EStorageBackend.Json)]
    public async Task ExplicitFallbackRequiresVerifiedStorageAndRestoresOnlyMatchingBase(EStorageBackend backend)
    {
        using MediaTestFiles files = new();
        SqliteRepository journal = new(Path.Combine(files.Root, "journal.db"));
        AvailabilityOptions options = Options(new(FallbackBackend: backend));
        using AvailabilityService service = new("a", options, journal, files.Root);
        await service.RememberConfirmedAsync(EStoragePurpose.Configuration, "config", NodeValue.FromObject(1), "r1", true).ConfigureAwait(true);
        AvailabilityWriteResult result = await service.HandleSaveFailureAsync(EStoragePurpose.Configuration, "config", NodeValue.FromObject(2), "r2", "r1", Guid.NewGuid(), true, static (_, _) => Task.FromResult(EOutcome.Succeeded)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.PersistenceOutcome);
        Assert.True(result.Applied);
        Assert.False(result.Current!.State.IsUnsaved);
        Assert.Equal("r1", result.Current.State.SavedRevision);
        Assert.Equal("r2", result.Current.State.RecoveryRevision);
        Assert.Equal(backend, result.Current.State.FallbackBackend);
        using AvailabilityService restarted = new("a", options, journal, files.Root);
        await restarted.RememberConfirmedAsync(EStoragePurpose.Configuration, "config", NodeValue.FromObject(1), "r1", true).ConfigureAwait(true);
        Assert.Equal(2, (await restarted.RestoreCandidateAsync(EStoragePurpose.Configuration, "config").ConfigureAwait(true))!.Value!.Json.GetInt32());
        Assert.True((await restarted.ActivateCandidateAsync(EStoragePurpose.Configuration, "config", static (_, _) => Task.FromResult(EOutcome.Succeeded)).ConfigureAwait(true)).Applied);
        using AvailabilityService superseded = new("a", options, journal, files.Root);
        await superseded.RememberConfirmedAsync(EStoragePurpose.Configuration, "config", NodeValue.FromObject(3), "r3", true).ConfigureAwait(true);
        Assert.Null(await superseded.RestoreCandidateAsync(EStoragePurpose.Configuration, "config").ConfigureAwait(true));
    }

    /// <summary>未保存候補は容量上限で黙って追い出しません。</summary>
    /// <returns>有限件数とバイトの確認です。</returns>
    [Fact]
    public async Task DirtyCandidateIsNotEvictedByNewData()
    {
        using MediaTestFiles files = new();
        using AvailabilityService service = new("a", Options(new(EAvailabilitySaveFailure.UseUnsaved)) with { MaximumItems = 1, MaximumBytes = 2048 }, new SqliteRepository(Path.Combine(files.Root, "state.db")), files.Root);
        await service.RememberConfirmedAsync(EStoragePurpose.Configuration, "first", NodeValue.FromObject(1), "r1", true).ConfigureAwait(true);
        await service.HandleSaveFailureAsync(EStoragePurpose.Configuration, "first", NodeValue.FromObject(2), "r2", "r1", Guid.NewGuid(), true, static (_, _) => Task.FromResult(EOutcome.Succeeded)).ConfigureAwait(true);
        AvailabilityValue second = await service.RememberConfirmedAsync(EStoragePurpose.Configuration, "second", NodeValue.FromObject(3), "r3", true).ConfigureAwait(true);
        Assert.Equal("Availability.CacheCapacity", second.State.Reason);
        Assert.Equal("first", Assert.Single(service.GetStates()).DatasetId);
        Assert.Equal(2, service.TryGetCurrent(EStoragePurpose.Configuration, "first")!.Value!.Json.GetInt32());
        AvailabilityWriteResult tooLarge = await service.HandleSaveFailureAsync(EStoragePurpose.Configuration, "first", NodeValue.FromObject(new string('x', 3000)), "large", "r1", Guid.NewGuid(), true).ConfigureAwait(true);
        Assert.Equal("Availability.CacheCapacity", tooLarge.Reason);
        Assert.Equal(2, service.TryGetCurrent(EStoragePurpose.Configuration, "first")!.Value!.Json.GetInt32());
    }

    /// <summary>configurationだけの明示方針を設定します。</summary>
    /// <returns>他用途の既定を変えない設定です。</returns>
    private static AvailabilityOptions Options(AvailabilityPolicy policy) => new(new Dictionary<EStoragePurpose, AvailabilityPolicy> { [EStoragePurpose.Configuration] = policy });
}
