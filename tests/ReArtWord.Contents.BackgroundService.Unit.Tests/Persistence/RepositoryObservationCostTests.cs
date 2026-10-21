using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>変更のない監視が大きな履歴本文を毎回復元せず、書込みも行わないことを測定します。</summary>
public sealed class RepositoryObservationCostTests(ITestOutputHelper output)
{
    /// <summary>同じ内容を繰り返し観測し、時間・割当量・保存ファイルの同一性を確認します。</summary>
    /// <returns>通常行と圧縮履歴の観測コストです。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MeasuresUnchangedLargeObservationsAsync(bool archived)
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "observation.db");
        SqliteRepository repository = new(path);
        const string scope = "peer:cost:workflow-results";
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string text = string.Concat(Enumerable.Repeat(Convert.ToBase64String(RandomNumberGenerator.GetBytes(768)), 128));
        for (int index = 0; index < 16; index++)
        {
            await repository.WriteLocalSnapshotAsync(scope, "item-" + index, NodeValue.FromObject(new { CompletedAt = now.AddDays(-4), text })).ConfigureAwait(true);
        }
        if (archived)
        {
            using HistoryArchiveService archive = new(repository);
            for (int index = 0; index < 8; index++)
            {
                await archive.MaintainAsync(new(), new(1, 10), now).ConfigureAwait(true);
            }
        }
        IReadOnlyList<DurableObservation> initial = await repository.ObserveAsync(scope, new Dictionary<string, string>()).ConfigureAwait(true);
        Assert.Equal(16, initial.Count);
        Dictionary<string, string> known = initial.ToDictionary(item => item.Key, item => item.Fingerprint, StringComparer.Ordinal);
        _ = await repository.ObserveAsync(scope, known).ConfigureAwait(true);
        byte[] beforeFile = SHA256.HashData(await File.ReadAllBytesAsync(path).ConfigureAwait(true));
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch watch = Stopwatch.StartNew();
        for (int index = 0; index < 10; index++)
        {
            IReadOnlyList<DurableObservation> unchanged = await repository.ObserveAsync(scope, known).ConfigureAwait(true);
            Assert.Equal(16, unchanged.Count);
            Assert.All(unchanged, item => Assert.Null(item.Entry));
        }
        double milliseconds = watch.Elapsed.TotalMilliseconds;
        long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        output.WriteLine(JsonSerializer.Serialize(new { archived, iterations = 10, rows = 16, payloadCharacters = text.Length * 16L, milliseconds, allocatedBytes = bytes }));
        Assert.InRange(bytes, 0, 2097152);
        Assert.Equal(beforeFile, SHA256.HashData(await File.ReadAllBytesAsync(path).ConfigureAwait(true)));
    }
}
