using System.Diagnostics;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>実運用DBを使わず、有限量の履歴を段階的に圧縮して容量と元結果を確認します。</summary>
public sealed class HistoryArchiveScaleTests(ITestOutputHelper output)
{
    /// <summary>明示実行では最大100万件まで増やせます。通常CIでは小さい同じ挙動を検査します。</summary>
    /// <returns>確定結果を保った圧縮・縮小の測定です。</returns>
    [Fact]
    public async Task BoundedBatchesArchiveHistoryAndPreserveSampledResults()
    {
        int count = 256;
        string setting = "CONTENTS_ARCHIVE_RECORDS";
        if (int.TryParse(Environment.GetEnvironmentVariable(setting), out int requested) && requested is >= 1 and <= 1000000)
        {
            count = requested;
        }
        string root = Path.GetFullPath("../../../../../.work/history-archive/" + Guid.NewGuid().ToString("N"), AppContext.BaseDirectory);
        Directory.CreateDirectory(root);
        PersistenceOptions persistence = new(EStorageBackend.Sqlite);
        IDurableRepository repository = await RepositoryFactory.CreateAsync(root, "fixture", persistence);
        List<string> keys = [];
        Stopwatch elapsed = Stopwatch.StartNew();
        for (int offset = 0; offset < count; offset += 500)
        {
            List<DurableMutation> mutations = [];
            for (int index = offset; index < Math.Min(count, offset + 500); index++)
            {
                string key = Guid.NewGuid().ToString("N");
                keys.Add(key);
                NodeValue value = NodeValue.FromObject(new
                {
                    Fingerprint = key,
                    InstanceId = Guid.NewGuid(),
                    ExpiresAt = DateTimeOffset.UtcNow.AddDays(-10),
                    Response = new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(new { index, text = new string('x', 256), marker = Guid.NewGuid() })),
                });
                mutations.Add(new("receipts:fixture", key, null, value));
            }
            Assert.Equal(EOutcome.Succeeded, (await repository.ApplyBatchAsync(Guid.NewGuid(), mutations)).Outcome);
        }
        double seedMilliseconds = elapsed.Elapsed.TotalMilliseconds;
        long before = new FileInfo(Path.Combine(root, "state.db")).Length;
        elapsed.Restart();
        using HistoryArchiveService archive = new(repository);
        HistoryArchiveStatus status = await archive.ReadStatusAsync();
        for (int batch = 0; batch < count / 20 + 100 && status.ArchivedRecords < count; batch++)
        {
            await archive.ArchiveOnceAsync(new(KeepDays: 0, MaximumHotRecords: 1), DateTimeOffset.UtcNow.AddDays(8));
            status = await archive.ReadStatusAsync();
        }
        double archiveMilliseconds = elapsed.Elapsed.TotalMilliseconds;
        Assert.True(count <= status.ArchivedRecords);
        foreach (int index in Enumerable.Range(0, Math.Min(count, 20)).Select(sample => sample * count / Math.Min(count, 20)))
        {
            DurableEntry row = (await repository.ReadAsync("receipts:fixture", keys[index]))!;
            Assert.Equal(index, row.Value!.Json.GetProperty("Response").GetProperty("Value").GetProperty("Json").GetProperty("index").GetInt32());
        }
        elapsed.Restart();
        HistoryArchiveStatus compacted = await HistoryOfflineMaintenance.RunAsync(root, "fixture", persistence, false);
        double compactMilliseconds = elapsed.Elapsed.TotalMilliseconds;
        Assert.True(compacted.DatabaseBytes < before);
        string report = JsonSerializer.Serialize(new { count, before, seedMilliseconds, archiveMilliseconds, compactMilliseconds, status, compacted });
        await File.WriteAllTextAsync(Path.Combine(root, "measurement.json"), report);
        output.WriteLine(report);
        output.WriteLine(root);
    }
}
