using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.ClusterSynchronization;

/// <summary>旧版向けの個別ID展開でも、欠番と元のキー表記を維持します。</summary>
public sealed class HistoryRangeCompatibilityTests
{
    /// <summary>既存の詳細結果を優先し、区間の欠番や別表記のキーを退役扱いにしません。</summary>
    [Fact]
    public void ExpansionPreservesIdsHolesAndExistingEvidence()
    {
        OperationIdSequence sequence = new();
        Guid first = sequence.Next(), second = sequence.Next(), gap = sequence.Next(), last = sequence.Next();
        RetiredIdRange start = RetiredIdRange.Operation(first, true), end = RetiredIdRange.Operation(second, true);
        string scope = "snapshots", key = last.ToString("D").ToUpperInvariant();
        RetiredOperation detailed = new(first, "original-fingerprint", true, EOutcome.Failed, "Persistence.Aborted");
        RepositorySnapshot snapshot = new([], [], RetiredOperations: [detailed],
            RetiredRanges: [start with { Last = end.Last }, RetiredIdRange.Operation(last, true), RetiredIdRange.Key(scope, key)!]);
        RepositorySnapshot expanded = HistoryRangeCompatibility.Expand(snapshot);
        Assert.Null(expanded.RetiredRanges);
        Assert.False(JsonSerializer.SerializeToElement(expanded).TryGetProperty(nameof(RepositorySnapshot.RetiredRanges), out _));
        Assert.Equal(detailed, expanded.RetiredOperations!.Single(item => item.Id == first));
        Assert.DoesNotContain(expanded.RetiredOperations!, item => item.Id == gap);
        Assert.Equal(new[] { first, second, last }.Order(), expanded.RetiredOperations!.Select(item => item.Id).Order());
        Assert.All(expanded.RetiredOperations!.Where(item => item.Id != first), item =>
        {
            Assert.Equal(EOutcome.Unknown, item.Outcome);
            Assert.Equal(RepositoryHistoryGuard.ExpiredReason, item.Reason);
            Assert.Empty(item.Fingerprint);
        });
        Assert.Equal(new RetiredHistoryKey(scope, key), Assert.Single(expanded.RetiredKeys!));
        RepositoryDocument restored = new();
        Assert.True(RepositoryMerge.Merge(restored, expanded));
        Assert.Equal(detailed, Assert.Single(restored.History!.RetiredOperations.Values));
        Assert.True(restored.History.RetiredRanges!.ContainsOperation(second));
        Assert.True(restored.History.RetiredRanges.ContainsOperation(last));
        Assert.False(restored.History.RetiredRanges.ContainsOperation(gap));
        Assert.True(restored.History.RetiredRanges.ContainsKey(scope, key));
        Assert.Empty(restored.History.RetiredKeys);
        Assert.Equal(1, RepositoryHistoryGuard.Read(restored)!.RetirementFormat);
    }

    /// <summary>展開上限を超えた場合は証拠を切り捨てず、同期相手の更新を要求します。</summary>
    [Fact]
    public void OversizedCompatibilityExportDoesNotDropRetirementProof()
    {
        RetiredIdRange range = RetiredIdRange.Operation(new OperationIdSequence().Next(), true) with { First = 0, Last = uint.MaxValue };
        SyncFailureException failure = Assert.Throws<SyncFailureException>(() => HistoryRangeCompatibility.Expand(new([], [], RetiredRanges: [range])));
        Assert.Equal("Sync.HistoryUpgradeRequired", failure.Message);
    }

    /// <summary>区間の端が変わったら同じ同期区分が変化し、差分からその証拠が脱落しません。</summary>
    [Fact]
    public void InventoryTracksRangesWithoutKeepingThemInUnchangedMetadata()
    {
        RetiredIdRange first = RetiredIdRange.Operation(new OperationIdSequence().Next(), true);
        RepositorySnapshot before = new([], [], RetiredRanges: [first]);
        RepositorySnapshot after = before with { RetiredRanges = [first with { Last = first.Last + 5 }] };
        string[] left = SyncInventory.Describe(before), right = SyncInventory.Describe(after);
        int bucket = SyncInventory.RetiredRangeBucket(first);
        Assert.NotEqual(left[bucket], right[bucket]);
        Assert.Single(Enumerable.Range(0, left.Length), index => !string.Equals(left[index], right[index], StringComparison.Ordinal));
        Assert.Empty(SyncInventory.Metadata(after).RetiredRanges!);
        Assert.Equal(after.RetiredRanges, SyncInventory.Difference(after, right, left).RetiredRanges);
    }
}
