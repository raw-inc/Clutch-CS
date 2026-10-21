using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Causality;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Replication;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>送信前の判断と局所コミットが原子的に保存され、確認待ちだけが巡回に残ることを検証します。</summary>
public sealed class ReplicationCommitTests
{
    /// <summary>保存媒体と用途ルーターが異なっても、準備の終了と判断の耐久性を揃えます。</summary>
    /// <returns>再オープン後の確定結果と、全参加者の確認前に巡回を失わないことです。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LocalCommitAndDecisionSurviveReopeningAsync(bool sqlite, bool routed)
    {
        using MediaTestFiles files = new();
        IDurableRepository repository = await CreateAsync(files.Root, sqlite, routed);
        DurableReceipt pending = await PrepareAsync(repository);
        DurableReceipt result = await new ReplicationJournal(repository).CommitAsync(pending with { Reason = ReplicationJournal.CommitDecided });
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        repository = await CreateAsync(files.Root, sqlite, routed);
        Assert.Equal(EOutcome.Succeeded, (await repository.ReadReceiptAsync(pending.OperationId))!.Outcome);
        Assert.Equal(ReplicationJournal.CommitDecided, (await new ReplicationJournal(repository).ReadAsync(pending.OperationId))!.Reason);
        IPreparedOperations operations = Assert.IsAssignableFrom<IPreparedOperations>(repository);
        Assert.Empty(await operations.ReadPreparedIdsAsync(Guid.Empty, 64));
        Assert.Equal(pending.OperationId, Assert.Single(await operations.ReadRecoveryIdsAsync(Guid.Empty, 64)));
        await new ReplicationJournal(repository).SaveAsync(result);
        Assert.Empty(await operations.ReadRecoveryIdsAsync(Guid.Empty, 64));
    }

    /// <summary>本文と判断の更新後に保存失敗を入れても、片方だけを確定しません。</summary>
    /// <returns>失敗後に元準備を維持し、同じ操作で安全に確定できることです。</returns>
    [Fact]
    public async Task ReceiptWriteFailureRollsBackBothDecisionAndValueAsync()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository repository = new(path);
        DurableReceipt pending = await PrepareAsync(repository);
        using (SqliteConnection connection = new("Pooling=False;Data Source=" + path))
        {
            await connection.OpenAsync();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER fail_commit_receipt BEFORE INSERT ON records WHEN NEW.kind='receipt' AND NEW.scope='shared' BEGIN SELECT RAISE(ABORT,'isolated failure'); END";
            await command.ExecuteNonQueryAsync();
        }
        ReplicationJournal journal = new(repository);
        await Assert.ThrowsAsync<SqliteException>(() => journal.CommitAsync(pending with { Reason = ReplicationJournal.CommitDecided }));
        Assert.Equal(ReplicationJournal.Preparing, (await journal.ReadAsync(pending.OperationId))!.Reason);
        Assert.NotNull(await repository.ReadPreparationAsync(pending.OperationId));
        Assert.Null(await repository.ReadReceiptAsync(pending.OperationId));
        Assert.Null(await repository.ReadAsync("workflow:atomic", "value"));
        using (SqliteConnection connection = new("Pooling=False;Data Source=" + path))
        {
            await connection.OpenAsync();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DROP TRIGGER fail_commit_receipt";
            await command.ExecuteNonQueryAsync();
        }
        Assert.Equal(EOutcome.Succeeded, (await journal.CommitAsync(pending with { Reason = ReplicationJournal.CommitDecided })).Outcome);
    }

    /// <summary>局所だけの準備を除外し、64件より多い共有準備も重複・欠落なく巡回します。</summary>
    /// <returns>本文ではなく識別子だけを扱う索引の境界です。</returns>
    [Fact]
    public async Task PreparedIndexPagesExcludeLocalOperationsAsync()
    {
        using MediaTestFiles files = new();
        SqliteRepository repository = new(Path.Combine(files.Root, "state.db"));
        List<Guid> expected = [];
        for (int index = 0; index < 80; index++)
        {
            Guid id = Guid.NewGuid();
            expected.Add(id);
            await repository.PrepareAsync(id, [new("workflow:page", index.ToString(System.Globalization.CultureInfo.InvariantCulture), null, NodeValue.FromObject(index))]);
        }
        await repository.PrepareAsync(Guid.NewGuid(), [new("peer:private", "value", null, NodeValue.FromObject(1))]);
        IPreparedOperations operations = repository;
        IReadOnlyList<Guid> first = await operations.ReadPreparedIdsAsync(Guid.Empty, 64);
        IReadOnlyList<Guid> second = await operations.ReadPreparedIdsAsync(first[^1], 64);
        Assert.Equal(64, first.Count);
        Assert.Equal(16, second.Count);
        Assert.Equal(expected.Order(), first.Concat(second));
        Assert.Empty(await operations.ReadPreparedIdsAsync(second[^1], 64));
    }

    /// <summary>送信元と同じ開始記録・準備を保存します。</summary>
    /// <returns>確定判断前の共有結果です。</returns>
    private static async Task<DurableReceipt> PrepareAsync(IDurableRepository repository, CancellationToken cancellationToken = default)
    {
        Guid id = Guid.NewGuid();
        DurableMutation[] mutations = [new("workflow:atomic", "value", null, NodeValue.FromObject(42))];
        DurableReceipt pending = new(id, RepositoryCausality.Fingerprint(mutations), EOutcome.Unknown, [], ReplicationJournal.Preparing);
        ReplicationStartResult started = await ReplicationStart.BeginAsync(repository, id, mutations,
            [new("cluster:operations", id.ToString("N"), null, NodeValue.FromObject(pending))], cancellationToken).ConfigureAwait(false);
        Assert.Equal(EOutcome.Succeeded, started.Initialized.Outcome);
        if (started.Prepared is null)
        {
            Assert.Equal(EOutcome.Succeeded, (await repository.PrepareAsync(id, started.Mutations, cancellationToken).ConfigureAwait(false)).Outcome);
        }
        return pending;
    }

    /// <summary>実保存媒体を同じ場所で再作成します。</summary>
    /// <returns>選択した用途ルーターまたは単一保存先です。</returns>
    private static async Task<IDurableRepository> CreateAsync(string root, bool sqlite, bool routed, CancellationToken cancellationToken = default)
    {
        if (routed)
        {
            return await RepositoryFactory.CreateAsync(root, "peer", new(EStorageBackend.Sqlite), cancellationToken).ConfigureAwait(false);
        }
        if (sqlite)
        {
            return new SqliteRepository(Path.Combine(root, "state.db"));
        }
        return new JsonFileRepository(Path.Combine(root, "state.json"));
    }
}
