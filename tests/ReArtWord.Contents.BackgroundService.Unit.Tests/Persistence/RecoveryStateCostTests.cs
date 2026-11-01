using System.Diagnostics;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>過去本文の件数が増えても、繰り返す復旧確認の割当と書込みを増やしません。</summary>
[Collection("Performance")]
public sealed class RecoveryStateCostTests(ITestOutputHelper output)
{
    /// <summary>用途ルーターを経由し、過去本文を読む明示書出しと現在状態の確認を区別します。</summary>
    /// <returns>履歴量ごとの復旧確認コストと共有内容の一致です。</returns>
    [Theory]
    [InlineData(128)]
    [InlineData(2048)]
    public async Task CurrentStateChecksStayBoundedAcrossHistorySizesAsync(int count)
    {
        using MediaTestFiles files = new();
        IDurableRepository repository = await RepositoryFactory.CreateAsync(files.Root, "p", new(EStorageBackend.Sqlite)).ConfigureAwait(true);
        NodeValue large = NodeValue.FromObject(new string('x', 8192));
        DurableReceipt[] receipts = Enumerable.Range(0, count).Select(index => new DurableReceipt(Guid.NewGuid(), "fp", EOutcome.Succeeded,
            [new("workflow:past", "value", index.ToString(System.Globalization.CultureInfo.InvariantCulture), large)])).ToArray();
        DurableEntry current = new("workflow:past", "value", "current", NodeValue.FromObject(42));
        Assert.True(await repository.ImportAsync(new([current], receipts)).ConfigureAwait(true));
        string expected = RepositoryStateVersion.Hash(await repository.ExportAsync().ConfigureAwait(true));
        DateTime beforeWrite = File.GetLastWriteTimeUtc(Path.Combine(files.Root, "state.db"));
        long before = GC.GetTotalAllocatedBytes(true);
        Stopwatch watch = Stopwatch.StartNew();
        for (int attempt = 0; attempt < 10; attempt++)
        {
            Assert.Equal(expected, await repository.GetSharedVersionAsync().ConfigureAwait(true));
            RepositorySnapshot state = await repository.ReadCurrentStateAsync().ConfigureAwait(true);
            Assert.Empty(state.Receipts);
            Assert.Equal(expected, RepositoryStateVersion.Hash(state));
        }
        long allocated = GC.GetTotalAllocatedBytes(true) - before;
        output.WriteLine($"receipts={count}, elapsedMs={watch.Elapsed.TotalMilliseconds:F1}, allocatedBytes={allocated}");
        Assert.InRange(allocated, 0, 4194304);
        Assert.Equal(beforeWrite, File.GetLastWriteTimeUtc(Path.Combine(files.Root, "state.db")));
        Assert.Equal(count, (await repository.ExportAsync().ConfigureAwait(true)).Receipts.Count);
    }
}
