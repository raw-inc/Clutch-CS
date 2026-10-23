using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Configuration;

/// <summary>有効版の確認に不要な保存候補を読まず、反映希望の変更は毎回検証します。</summary>
public sealed class OperationalPreparationTests
{
    /// <summary>反映希望がある場合、未反映の保存候補の読取りに依存しません。</summary>
    /// <returns>同じ確認済み設定を継続できることです。</returns>
    [Fact]
    public async Task ConfirmedActivationDoesNotRereadSavedCandidateAsync()
    {
        using MediaTestFiles files = new();
        SqliteRepository storage = new(Path.Combine(files.Root, "state.db"));
        ClusterOperationalSnapshot initial = Snapshot("UTC");
        ClusterConfigurationStored draft = new(Snapshot("Asia/Tokyo"), EActivationPolicy.Explicit);
        await storage.ApplyBatchAsync(Guid.NewGuid(),
            [new(ClusterConfigurationStore.ActivationScope, ClusterConfigurationStore.Id, null, NodeValue.FromObject(initial)),
            new(ClusterConfigurationStore.SavedScope, ClusterConfigurationStore.Id, null, NodeValue.FromObject(draft))]);
        FailingRepository repository = new(storage)
        {
            Reading = static (scope, _) =>
            {
                if (string.Equals(scope, ClusterConfigurationStore.SavedScope, StringComparison.Ordinal))
                {
                    throw new IOException("The saved candidate is not part of activation reconciliation.");
                }
            },
        };
        ClusterOperationalEffective effective = new(repository, "peer");
        Assert.Equal(ELocalPreparationState.Prepared, (await effective.PrepareAsync((_, _) => Task.FromResult(true))).State);
        Assert.Equal(ELocalPreparationState.Prepared, (await effective.PrepareAsync((_, _) => Task.FromResult(false))).State);
        Assert.Equal(initial.Revision, effective.Capture()!.Revision);
    }

    /// <summary>反映希望がない場合だけ、未作成と保存済み未反映を区別します。</summary>
    /// <returns>初期参加の判定を維持することです。</returns>
    [Theory]
    [InlineData(false, ELocalPreparationState.Empty)]
    [InlineData(true, ELocalPreparationState.Unavailable)]
    public async Task MissingActivationDistinguishesUninitializedAndSavedAsync(bool saved, ELocalPreparationState expected)
    {
        using MediaTestFiles files = new();
        SqliteRepository repository = new(Path.Combine(files.Root, "state.db"));
        if (saved)
        {
            ClusterConfigurationStored candidate = new(Snapshot("UTC"), EActivationPolicy.Explicit);
            await repository.ApplyBatchAsync(Guid.NewGuid(), [new(ClusterConfigurationStore.SavedScope, ClusterConfigurationStore.Id, null, NodeValue.FromObject(candidate))]);
        }
        ClusterOperationalEffective effective = new(repository, "peer");
        Assert.Equal(expected, (await effective.PrepareAsync((_, _) => Task.FromResult(true))).State);
        Assert.Null(effective.Capture());
    }

    /// <summary>別の保存実体による反映希望の変更を読み、保存確認と本文検証を省きません。</summary>
    /// <returns>未確認・正常・不正な新版の区別です。</returns>
    [Fact]
    public async Task EveryRefreshValidatesChangedActivationAsync()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository repository = new(path);
        ClusterOperationalEffective effective = new(repository, "peer");
        await ReplaceAsync(repository, Snapshot("UTC"));
        Assert.Equal(ELocalPreparationState.Prepared, (await effective.PrepareAsync((_, _) => Task.FromResult(true))).State);
        SqliteRepository other = new(path);
        await ReplaceAsync(other, Snapshot("Asia/Tokyo"));
        Assert.Equal(ELocalPreparationState.Unavailable, (await effective.PrepareAsync((_, _) => Task.FromResult(false))).State);
        Assert.Equal("UTC", effective.Capture()!.Settings.TimeZoneId);
        Assert.Equal(ELocalPreparationState.Prepared, (await effective.PrepareAsync((_, _) => Task.FromResult(true))).State);
        Assert.Equal("Asia/Tokyo", effective.Capture()!.Settings.TimeZoneId);
        await ReplaceAsync(other, Snapshot("UTC") with { Revision = "invalid" });
        Assert.Equal(ELocalPreparationState.Unavailable, (await effective.PrepareAsync((_, _) => Task.FromResult(true))).State);
        Assert.Equal("Asia/Tokyo", effective.Capture()!.Settings.TimeZoneId);
    }

    /// <summary>反映希望を実際の比較更新で置き換えます。</summary>
    /// <returns>保存確認の完了です。</returns>
    private static async Task ReplaceAsync(SqliteRepository repository, ClusterOperationalSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        DurableEntry? previous = await repository.ReadAsync(ClusterConfigurationStore.ActivationScope, ClusterConfigurationStore.Id, cancellationToken).ConfigureAwait(false);
        Assert.Equal(EOutcome.Succeeded, (await repository.ApplyBatchAsync(Guid.NewGuid(),
            [new(ClusterConfigurationStore.ActivationScope, ClusterConfigurationStore.Id, previous?.Version, NodeValue.FromObject(snapshot))], cancellationToken).ConfigureAwait(false)).Outcome);
    }

    /// <summary>共通設定の本文に対応する版を作成します。</summary>
    /// <returns>有効な反映希望です。</returns>
    private static ClusterOperationalSnapshot Snapshot(string timeZone)
    {
        ClusterOperationalSettings settings = new(timeZone, EFileConflictPolicy.Fail, EFileInternalFailurePolicy.Keep, new Dictionary<string, IReadOnlyList<string>>());
        return new(settings, ContractJson.Hash(settings), DateTimeOffset.UtcNow);
    }
}
