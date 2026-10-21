using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Recovery;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>背景取得の失敗・再開・保持境界が、現在版と進捗の意味を壊さないことを確認します。</summary>
public sealed class StagedHistoryBoundaryTests
{
    /// <summary>圧縮位置だけが異なる現在のWF定義を再公開し、現在版の不一致を残しません。</summary>
    /// <returns>本文を再圧縮せず、以後の版照合が一致することです。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurrentWorkflowDefinitionIsRestoredFromArchivedHistory(bool staged)
    {
        using MediaTestFiles sourceFiles = new();
        using MediaTestFiles targetFiles = new();
        IDurableRepository source = await RepositoryFactory.CreateAsync(sourceFiles.Root, "source", new());
        IDurableRepository target = await RepositoryFactory.CreateAsync(targetFiles.Root, "target", new());
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await source.ApplyBatchAsync(Guid.NewGuid(), [new("workflow.history:editor", "active", null, NodeValue.FromObject(new { Revision = "active", SavedAt = now.AddDays(-2) }))]);
        Assert.True(await target.ImportAsync(await source.ExportAsync()));
        using HistoryArchiveService archive = new(target);
        for (int attempt = 0; attempt < 8; attempt++)
        {
            await archive.MaintainAsync(new(), new(RawDays: 0, TotalDays: 30), now.AddMinutes(20));
        }
        using SqliteConnection connection = new("Pooling=False;Data Source=" + Path.Combine(targetFiles.Root, "state.db"));
        await connection.OpenAsync();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM history_index WHERE scope='workflow.history:editor'";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
        await source.ApplyBatchAsync(Guid.NewGuid(), [new("workflows", "editor", null, NodeValue.FromObject(new { Revision = "active" }))]);
        RepositorySnapshot snapshot = await source.ExportAsync();
        if (staged)
        {
            snapshot = await RecoveryRepository.Create(source)!.ReadAsync("source", now.AddMinutes(2));
        }
        Assert.True(await target.ImportAsync(snapshot));
        Assert.Equal(0L, await command.ExecuteScalarAsync());
        Assert.Equal("active", (await target.ReadAsync("workflow.history:editor", "active"))!.Value!.Json.GetProperty("Revision").GetString());

        // 先に保持境界だけが存在した相手も、双方向の現在状態交換後に同じ版になります。
        Assert.True(await source.ImportAsync(await RecoveryRepository.Create(target)!.ReadAsync("target", now.AddMinutes(2))));
        Assert.Equal(await source.GetSharedVersionAsync(), await target.GetSharedVersionAsync());
    }

    /// <summary>同じ取込で既存対象が完了して新しい対象が増えても、件数を相殺しません。</summary>
    /// <returns>途中再起動後も同じ分母・分子を維持した進捗です。</returns>
    [Fact]
    public async Task ProgressCountsCompletedAndAddedRecordsAcrossRestart()
    {
        using MediaTestFiles sourceFiles = new();
        using MediaTestFiles targetFiles = new();
        IDurableRepository source = await RepositoryFactory.CreateAsync(sourceFiles.Root, "source", new());
        IDurableRepository target = await RepositoryFactory.CreateAsync(targetFiles.Root, "target", new());
        Guid first = Guid.NewGuid(), second = Guid.NewGuid(), third = Guid.NewGuid();
        await source.AbortPreparedAsync(first);
        await source.AbortPreparedAsync(second);
        RecoveryRepository outgoing = RecoveryRepository.Create(source)!;
        RepositorySnapshot original = await outgoing.ReadAsync("source", DateTimeOffset.UtcNow.AddMinutes(2));
        Assert.True(await target.ImportAsync(original));
        await source.AbortPreparedAsync(third);
        RepositorySnapshot extended = await outgoing.ReadAsync("source", DateTimeOffset.UtcNow.AddMinutes(2));
        Assert.True(await target.ImportAsync(extended with { Receipts = [(await source.ReadReceiptAsync(first))!] }));
        target = await RepositoryFactory.CreateAsync(targetFiles.Root, "target", new());
        RecoveryRepository incoming = RecoveryRepository.Create(target)!;
        HistorySynchronizationStatus progress = await incoming.StatusAsync();
        Assert.Equal(3, progress.Total);
        Assert.Equal(2, progress.Pending);
        Assert.Equal(1, progress.Completed);
        Assert.InRange(progress.ProgressPercent!.Value, 33, 34);
        (_, IReadOnlyList<RecoveryRecord> remaining) = await incoming.PendingAsync();
        Assert.Equal(2, await incoming.FillAsync(await outgoing.ReadBodiesAsync(remaining)));
        Assert.Equal(100, (await incoming.StatusAsync()).ProgressPercent);
    }

    /// <summary>従来同期から届いた確定結果も元の検査値を照合し、未取得状態を残しません。</summary>
    /// <returns>不一致拒否、正しい全取込と旧対応版へ戻る保存境界です。</returns>
    [Fact]
    public async Task FullLegacySnapshotCanCompletePendingMatchingReceipts()
    {
        using MediaTestFiles sourceFiles = new();
        using MediaTestFiles targetFiles = new();
        IDurableRepository source = await RepositoryFactory.CreateAsync(sourceFiles.Root, "source", new());
        IDurableRepository target = await RepositoryFactory.CreateAsync(targetFiles.Root, "target", new());
        await source.AbortPreparedAsync(Guid.NewGuid());
        RepositorySnapshot full = await source.ExportAsync();
        Assert.True(await target.ImportAsync(await RecoveryRepository.Create(source)!.ReadAsync("source", DateTimeOffset.UtcNow.AddMinutes(2))));
        await Assert.ThrowsAsync<InvalidDataException>(() => target.ImportAsync(full with { Receipts = [full.Receipts[0] with { Outcome = EOutcome.Succeeded }] }).AsTask());
        Assert.Equal(1, (await RecoveryRepository.Create(target)!.StatusAsync()).Pending);
        Assert.True(await target.ImportAsync(full));
        Assert.Equal(0, (await RecoveryRepository.Create(target)!.StatusAsync()).Pending);
        Assert.Single((await target.ExportAsync()).Receipts);
        using SqliteConnection connection = new("Pooling=False;Data Source=" + Path.Combine(targetFiles.Root, "state.db"));
        await connection.OpenAsync();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM repository_identity";
        Assert.Equal(3L, await command.ExecuteScalarAsync());
    }

    /// <summary>取れない先頭の履歴だけを繰り返さず、後続を別の取得元から補完できます。</summary>
    /// <returns>有限の一巡、問題を保持した部分完了と、再起動後の再開です。</returns>
    [Fact]
    public async Task UnavailableRecordsDoNotStarveLaterHistoryAndResumeAfterRestart()
    {
        using MediaTestFiles sourceFiles = new();
        using MediaTestFiles targetFiles = new();
        IDurableRepository source = await RepositoryFactory.CreateAsync(sourceFiles.Root, "source", new());
        IDurableRepository target = await RepositoryFactory.CreateAsync(targetFiles.Root, "target", new());
        for (int index = 0; index < 65; index++)
        {
            await source.AbortPreparedAsync(Guid.NewGuid());
        }
        RecoveryRepository outgoing = RecoveryRepository.Create(source)!, incoming = RecoveryRepository.Create(target)!;
        Assert.True(await target.ImportAsync(await outgoing.ReadAsync("source", DateTimeOffset.UtcNow.AddMinutes(2))));
        HashSet<Guid> visited = [];
        for (int round = 0; round < 3; round++)
        {
            (_, IReadOnlyList<RecoveryRecord> pending) = await incoming.PendingAsync();
            visited.UnionWith(pending.Select(reference => reference.OperationId));
            await incoming.FillAsync(pending.Select(reference => new RecoveryRecordBody(reference, null, "Sync.HistoryUnavailable")).ToArray());
        }
        Assert.Equal(65, visited.Count);
        (_, IReadOnlyList<RecoveryRecord> recovered) = await incoming.PendingAsync();
        Assert.Equal(recovered.Count, await incoming.FillAsync(await outgoing.ReadBodiesAsync(recovered)));
        HistorySynchronizationStatus before = await incoming.StatusAsync();
        target = await RepositoryFactory.CreateAsync(targetFiles.Root, "target", new());
        incoming = RecoveryRepository.Create(target)!;
        Assert.Equal(before, await incoming.StatusAsync());
        while (0 < (await incoming.StatusAsync()).Pending)
        {
            (_, IReadOnlyList<RecoveryRecord> pending) = await incoming.PendingAsync();
            Assert.Equal(pending.Count, await incoming.FillAsync(await outgoing.ReadBodiesAsync(pending)));
        }
        Assert.Null((await incoming.StatusAsync()).Problem);
    }
}
