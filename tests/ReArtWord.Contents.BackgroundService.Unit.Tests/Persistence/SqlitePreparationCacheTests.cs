using System.Diagnostics;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Testing.Performance;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>準備待ちの反復で本文を再復元せず、変更・削除・周期検査には追従することを検証します。</summary>
[Collection("Performance")]
public sealed class SqlitePreparationCacheTests(ITestOutputHelper output)
{
    /// <summary>同じ大きい準備へ複数要求が到着しても、冷たい本文検証を要求数分重ねません。</summary>
    /// <returns>並行照会の同一性と、全スレッド合計の追加割当量です。</returns>
    [Fact]
    public async Task ConcurrentColdReadsShareOnePayloadVerificationAsync()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository repository = new(path);
        Guid id = Guid.NewGuid();
        string fingerprint = (await repository.PrepareAsync(id, [new("workflow:prepared", "large", null, NodeValue.FromObject(new string('x', 2097152)))])).Fingerprint;
        SqlitePreparationCache cache = new();
        long allocated = GC.GetTotalAllocatedBytes(true);
        Parallel.For(0, 4, new ParallelOptions { MaxDegreeOfParallelism = 4 }, _ => Assert.Equal(fingerprint, cache.Read(path, id, CancellationToken.None)));
        allocated = GC.GetTotalAllocatedBytes(true) - allocated;
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { concurrentReaders = 4, allocatedBytes = allocated }));
        Assert.InRange(allocated, 0, 33554432);
    }

    /// <summary>同じ準備の指紋照会と共有版照会を反復しても、大きな本文を繰り返し割り当てません。</summary>
    /// <returns>内容を維持した照会の割当量・論理I/Oです。</returns>
    [Fact]
    public async Task RepeatedPreparationProbesDoNotMaterializeThePayloadAsync()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository repository = new(path);
        Guid id = Guid.NewGuid();
        string fingerprint = (await repository.PrepareAsync(id, [new("workflow:prepared", "large", null, NodeValue.FromObject(new string('x', 2097152)))])).Fingerprint;
        MutableClock clock = new();
        SqlitePreparationCache cache = new(clock);
        SqliteCurrentVersion version = new(path, clock, cache);
        string expected = version.Read(CancellationToken.None);
        Assert.Equal(fingerprint, cache.Read(path, id, CancellationToken.None));
        ProcessResourceSample before = ProcessResourceSample.Capture(Environment.ProcessId);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch watch = Stopwatch.StartNew();
        for (int index = 0; index < 12; index++)
        {
            Assert.Equal(fingerprint, cache.Read(path, id, CancellationToken.None));
            Assert.Equal(expected, version.Read(CancellationToken.None));
        }
        watch.Stop();
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        ProcessResourceSample after = ProcessResourceSample.Capture(Environment.ProcessId);
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { allocatedBytes = allocated, milliseconds = watch.Elapsed.TotalMilliseconds,
            readBytes = after.ReadBytes - before.ReadBytes, writeBytes = after.WriteBytes - before.WriteBytes }));
        Assert.InRange(allocated, 0, 4194304);
        if (before.ReadBytes is { } readBefore && after.ReadBytes is { } readAfter)
        {
            Assert.InRange(readAfter - readBefore, 0, 4194304);
        }
        if (before.WriteBytes is { } writeBefore && after.WriteBytes is { } writeAfter)
        {
            Assert.InRange(writeAfter - writeBefore, 0, 65536);
        }
    }

    /// <summary>確認済み準備が消えたときは、キャッシュ期限を待たず共有版から外します。</summary>
    /// <returns>取消後の同じ識別子の読取りです。</returns>
    [Fact]
    public async Task CompletedPreparationIsRemovedImmediatelyAsync()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository repository = new(path);
        Guid id = Guid.NewGuid();
        await repository.PrepareAsync(id, [new("workflow:prepared", "value", null, NodeValue.FromObject(1))]);
        SqlitePreparationCache cache = new();
        Assert.NotNull(cache.Read(path, id, CancellationToken.None));
        Assert.Equal(EOutcome.Failed, (await repository.AbortPreparedAsync(id)).Outcome);
        Assert.Null(cache.Read(path, id, CancellationToken.None));
        using SqliteSession session = new(path, write: false);
        Assert.Empty(cache.SharedIds(session, CancellationToken.None));
    }

    /// <summary>派生索引の欠落を準備なしと解釈せず、再オープンでは現存する準備から補完します。</summary>
    /// <returns>未確定操作の見落としを防ぐ境界です。</returns>
    [Fact]
    public async Task MissingProjectionDoesNotHideAnExistingPreparationAsync()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository repository = new(path);
        Guid id = Guid.NewGuid();
        string fingerprint = (await repository.PrepareAsync(id, [new("workflow:prepared", "value", null, NodeValue.FromObject(1))])).Fingerprint;
        SqlitePreparationCache cache = new();
        Assert.Equal(fingerprint, cache.Read(path, id, CancellationToken.None));
        using (SqliteConnection connection = new("Pooling=False;Data Source=" + path))
        {
            await connection.OpenAsync();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DELETE FROM preparation_versions WHERE operation=$id";
            command.Parameters.AddWithValue("$id", id.ToString("N"));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        Assert.Throws<InvalidDataException>(() => cache.Read(path, id, CancellationToken.None));
        repository = new(path);
        Assert.Equal(fingerprint, await repository.ReadPreparationAsync(id));
    }

    /// <summary>内容版の変更は直ちに検査し、内容版を変えない破損も既存と同じ30秒境界で検出します。</summary>
    /// <returns>キャッシュの存在を整合性保証の代わりにしない境界です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedOrAgedPreparationIsVerifiedAgainAsync(bool changeHash)
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository repository = new(path);
        Guid id = Guid.NewGuid();
        string fingerprint = (await repository.PrepareAsync(id, [new("workflow:prepared", "value", null, NodeValue.FromObject("one"))])).Fingerprint;
        MutableClock clock = new();
        SqlitePreparationCache cache = new(clock);
        Assert.Equal(fingerprint, cache.Read(path, id, CancellationToken.None));
        using (SqliteConnection connection = new("Pooling=False;Data Source=" + path))
        {
            await connection.OpenAsync();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE records SET payload=replace(payload,$before,$after),hash=CASE WHEN $change THEN 'invalid-hash' ELSE hash END WHERE kind='prepared' AND key=$id";
            command.Parameters.AddWithValue("$id", id.ToString("N"));
            command.Parameters.AddWithValue("$before", "\"one\"");
            command.Parameters.AddWithValue("$after", "\"two\"");
            command.Parameters.AddWithValue("$change", changeHash);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        if (!changeHash)
        {
            Assert.Equal(fingerprint, cache.Read(path, id, CancellationToken.None));
            clock.Advance(TimeSpan.FromSeconds(31));
        }
        Assert.Throws<InvalidDataException>(() => cache.Read(path, id, CancellationToken.None));
    }

    /// <summary>待機せず、本文再検査の経過時間を進めます。</summary>
    private sealed class MutableClock : TimeProvider
    {
        private long timestamp;

        /// <summary>経過時間の単位を固定します。</summary>
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        /// <summary>検査の経過時間を返します。</summary>
        /// <returns>試験内の単調時刻です。</returns>
        public override long GetTimestamp() => timestamp;

        /// <summary>指定した時間だけ検査境界を進めます。</summary>
        internal void Advance(TimeSpan duration) => timestamp += duration.Ticks;
    }
}
