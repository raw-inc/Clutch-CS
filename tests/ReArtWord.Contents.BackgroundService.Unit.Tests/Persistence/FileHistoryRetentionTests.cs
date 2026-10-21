using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Retention;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>ファイル操作の終了履歴だけへ共通保持を適用し、予約・退避・未確認結果を守ります。</summary>
public sealed class FileHistoryRetentionTests
{
    /// <summary>両保存媒体で終了済み本文を圧縮・期限削除し、未知の予約と実処理を残します。</summary>
    /// <returns>期限後の再利用拒否と現在値の維持です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedFileHistoryRollsWithoutRetiringPendingOperationsAsync(bool json)
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        IDurableRepository repository = json ? new JsonFileRepository(path)
                                             : new SqliteRepository(path);
        using HistoryArchiveService history = new(repository);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DataRetentionSettings policy = new(1, 3);
        (string Scope, NodeValue Completed, NodeValue Pending)[] values = [
            ("peer:test:file-gate-requests", NodeValue.FromObject(new { Outcome = EOutcome.Succeeded, CompletedAt = now }), NodeValue.FromObject(new { Kind = "file.gate.release", ExpiresAt = now })),
            ("peer:test:file-recovery", NodeValue.FromObject(new { execution = new { Outcome = EOutcome.Unknown }, release = new { Outcome = EOutcome.Succeeded } }),
                NodeValue.FromObject(new { execution = new { Outcome = EOutcome.Unknown }, release = new { Outcome = EOutcome.Unknown } })),
            ("peer:test:file-prepared", NodeValue.FromObject(new { Released = true }), NodeValue.FromObject(new { Released = false })),
            ("file:transactions", NodeValue.FromObject(new { Lease = new { Id = Guid.NewGuid() }, Result = new { Outcome = EOutcome.Succeeded, ReservationRelease = new { Outcome = EOutcome.Succeeded } } }),
                NodeValue.FromObject(new { Lease = new { Id = Guid.NewGuid() }, Result = new { Outcome = EOutcome.Unknown, ReservationRelease = new { Outcome = EOutcome.NotExecuted } } }))];
        foreach ((string scope, NodeValue completed, NodeValue pending) in values)
        {
            await repository.ApplyBatchAsync(Guid.NewGuid(), [new(scope, "ended", null, completed), new(scope, "pending", null, pending)]);
        }
        NodeValue active = NodeValue.FromObject(new { one = new { Id = Guid.NewGuid(), ReservationId = Guid.NewGuid() } });
        await repository.ApplyBatchAsync(Guid.NewGuid(), [new("file:reservations", "active", null, active)]);
        await MaintainAsync(history, policy, now);
        await MaintainAsync(history, policy, now.AddDays(2));
        foreach ((string scope, NodeValue completed, NodeValue _) in values)
        {
            Assert.True(JsonElement.DeepEquals(completed.Json, (await repository.ReadAsync(scope, "ended"))!.Value!.Json));
        }
        await MaintainAsync(history, policy, now.AddDays(5));
        foreach ((string scope, NodeValue _, NodeValue pending) in values)
        {
            Assert.True(await repository.IsHistoryRetiredAsync(scope, "ended"));
            Assert.True(JsonElement.DeepEquals(pending.Json, (await repository.ReadAsync(scope, "pending"))!.Value!.Json));
            Assert.NotEqual(EOutcome.Succeeded, (await repository.ApplyBatchAsync(Guid.NewGuid(), [new(scope, "ended", null, pending)])).Outcome);
        }
        Assert.True(JsonElement.DeepEquals(active.Json, (await repository.ReadAsync("file:reservations", "active"))!.Value!.Json));
    }

    /// <summary>初回分類と本文保守を有限単位で進めます。</summary>
    /// <returns>この日付での保守完了です。</returns>
    private static async Task MaintainAsync(HistoryArchiveService history, DataRetentionSettings policy, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        for (int index = 0; index < 6; index++)
        {
            await history.MaintainAsync(new(BatchRecords: 100), policy, now, cancellationToken).ConfigureAwait(false);
        }
    }
}
