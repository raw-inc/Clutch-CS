using Microsoft.Data.Sqlite;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Causality;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Replication;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>複製開始の一括保存で、再起動・競合・例外時の境界を維持することを検証します。</summary>
public sealed class ReplicationStartTests
{
    private static readonly string[] members = ["a", "b"];

    /// <summary>開始だけでは共有値を公開せず、再起動後も同じ因果情報で確定・中止します。</summary>
    /// <returns>実SQLiteの準備と元保存先を使った再開結果です。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PreparedStartSurvivesRestartWithoutPublishingBeforeCommit(bool routed, bool abort)
    {
        using MediaTestFiles files = new();
        IDurableRepository repository = await CreateAsync(files.Root, routed).ConfigureAwait(true);
        string version = await repository.GetSharedVersionAsync();
        Guid id = Guid.NewGuid();
        DurableMutation[] mutations = [new("workflows", "w", null, NodeValue.FromObject(42))];
        ReplicationStartResult started = await ReplicationStart.BeginAsync(repository, id, mutations, Metadata(id, mutations));
        Assert.Equal(EOutcome.Succeeded, started.Initialized.Outcome);
        Assert.Equal(EOutcome.Succeeded, started.Prepared?.Outcome);
        Assert.Null(await repository.ReadAsync("workflows", "w"));
        Assert.Equal(version, await repository.GetSharedVersionAsync());
        IDurableRepository restarted = await CreateAsync(files.Root, routed).ConfigureAwait(true);
        Assert.Equal(RepositoryCausality.Fingerprint(mutations), await restarted.ReadPreparationAsync(id));
        Assert.NotNull(await restarted.ReadAsync("cluster:replication-members", id.ToString("N")));
        Assert.Equal(EOutcome.Unknown, (await restarted.ReadAsync("cluster:operations", id.ToString("N")))!.Value!.Json.Deserialize<DurableReceipt>()!.Outcome);
        if (abort)
        {
            Assert.Equal("Persistence.Aborted", (await restarted.AbortPreparedAsync(id)).Reason);
            Assert.Equal("Persistence.Aborted", (await restarted.CommitAsync(id)).Reason);
            Assert.Null(await restarted.ReadAsync("workflows", "w"));
        }
        else
        {
            DurableReceipt committed = await restarted.CommitAsync(id);
            Assert.Equal(EOutcome.Succeeded, committed.Outcome);
            Assert.Equal(42, Assert.Single(committed.Entries).Value!.Json.GetInt32());
            Assert.Equal(started.Mutations[0].CausalVersion, Assert.Single((await restarted.ExportAsync()).CausalEntries!).Clock);
            Assert.Equal(ContractJson.Hash(committed), ContractJson.Hash(await restarted.CommitAsync(id)));
        }
        Assert.False(await restarted.HasPendingWriteAsync("workflows", "w"));
        Guid nextId = Guid.NewGuid();
        DurableMutation[] nextMutations = [new("workflows", "next", null, NodeValue.FromObject(43))];
        ReplicationStartResult next = await ReplicationStart.BeginAsync(restarted, nextId, nextMutations, Metadata(nextId, nextMutations));
        Assert.NotEqual(started.Mutations[0].CausalVersion, next.Mutations[0].CausalVersion);
        Assert.True(CausalClock.Includes(next.Mutations[0].CausalVersion!, started.Mutations[0].CausalVersion!));
        Assert.Equal(EOutcome.Succeeded, (await restarted.CommitAsync(nextId)).Outcome);
    }

    /// <summary>準備行の書込み失敗では参加者・位置・所有先も残さず、別操作で復旧できます。</summary>
    /// <returns>SQLの途中例外を注入した後の、再起動した保存領域です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparationWriteFailureRollsBackTheWholeStart(bool routed)
    {
        using MediaTestFiles files = new();
        IDurableRepository repository = await CreateAsync(files.Root, routed).ConfigureAwait(true);
        string originalOwners = ContractJson.Hash(await repository.ListAsync("peer:persistence-routing"));
        Guid id = Guid.NewGuid();
        DurableMutation[] mutations = [new("workflows", "w", null, NodeValue.FromObject(42))];
        await SetPreparationFailureAsync(files.Root, true).ConfigureAwait(true);
        await Assert.ThrowsAsync<SqliteException>(() => ReplicationStart.BeginAsync(repository, id, mutations, Metadata(id, mutations)).AsTask()).ConfigureAwait(true);
        IDurableRepository restarted = await CreateAsync(files.Root, routed).ConfigureAwait(true);
        Assert.Null(await restarted.ReadAsync("cluster:replication-members", id.ToString("N")));
        Assert.Null(await restarted.ReadAsync("cluster:operations", id.ToString("N")));
        Assert.Null(await restarted.ReadAsync(RepositoryCausality.Scope, "position"));
        Assert.Null(await restarted.ReadAsync("peer:persistence-routing", "owner:" + id.ToString("N")));
        Assert.Equal(originalOwners, ContractJson.Hash(await restarted.ListAsync("peer:persistence-routing")));
        Assert.Null(await restarted.ReadReceiptAsync(id));
        Assert.Null(await restarted.ReadPreparationAsync(id));
        Assert.False(await restarted.HasPendingWriteAsync("workflows", "w"));
        await SetPreparationFailureAsync(files.Root, false).ConfigureAwait(true);
        Guid fresh = Guid.NewGuid();
        Assert.Equal(EOutcome.Succeeded, (await ReplicationStart.BeginAsync(restarted, fresh, mutations, Metadata(fresh, mutations))).Prepared?.Outcome);
        Assert.Equal(EOutcome.Succeeded, (await restarted.CommitAsync(fresh)).Outcome);
    }

    /// <summary>開始前の取消では参加者・予約・所有先を作らず、別操作の保存を妨げません。</summary>
    /// <returns>取消後の実保存先が未変更であることの確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDoesNotLeaveAStartOrReservation(bool routed)
    {
        using MediaTestFiles files = new();
        IDurableRepository repository = await CreateAsync(files.Root, routed).ConfigureAwait(true);
        Guid id = Guid.NewGuid();
        DurableMutation[] mutations = [new("workflows", "w", null, NodeValue.FromObject(42))];
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync().ConfigureAwait(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReplicationStart.BeginAsync(repository, id, mutations, Metadata(id, mutations), cancelled.Token).AsTask()).ConfigureAwait(true);
        Assert.Null(await repository.ReadAsync("cluster:replication-members", id.ToString("N")));
        Assert.Null(await repository.ReadAsync("cluster:operations", id.ToString("N")));
        Assert.False(await repository.HasPendingWriteAsync("workflows", "w"));
        Assert.Equal(EOutcome.Succeeded, (await repository.ApplyBatchAsync(Guid.NewGuid(), mutations)).Outcome);
    }

    /// <summary>既存操作の参加者を上書きせず、新しい変更順序や予約も作りません。</summary>
    /// <returns>開始記録のCAS拒否と、変更を開始していない状態です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingStartRejectsReplacementBeforeStamping(bool routed)
    {
        using MediaTestFiles files = new();
        IDurableRepository repository = await CreateAsync(files.Root, routed).ConfigureAwait(true);
        Guid id = Guid.NewGuid();
        await repository.ApplyBatchAsync(Guid.NewGuid(), [new("cluster:operations", id.ToString("N"), null, NodeValue.FromObject("original"))]);
        DurableMutation[] mutations = [new("workflows", "w", null, NodeValue.FromObject(42))];
        ReplicationStartResult started = await ReplicationStart.BeginAsync(repository, id, mutations, Metadata(id, mutations));
        Assert.Equal(EOutcome.Failed, started.Initialized.Outcome);
        Assert.Null(started.Prepared);
        Assert.Empty(started.Mutations);
        Assert.Null(await repository.ReadAsync("cluster:replication-members", id.ToString("N")));
        Assert.Equal("original", (await repository.ReadAsync("cluster:operations", id.ToString("N")))!.Value!.GetString());
        Assert.Null(await repository.ReadAsync(RepositoryCausality.Scope, "position"));
        Assert.False(await repository.HasPendingWriteAsync("workflows", "w"));
    }

    /// <summary>別操作の予約を上書きせず、元の準備だけをそのまま確定できます。</summary>
    /// <returns>同じキーの競合と既存予約の維持です。</returns>
    [Fact]
    public async Task ConflictingPreparationPreservesTheOriginalReservation()
    {
        using MediaTestFiles files = new();
        IDurableRepository repository = await CreateAsync(files.Root, true).ConfigureAwait(true);
        Guid original = Guid.NewGuid(), conflicting = Guid.NewGuid();
        DurableMutation[] mutations = [new("workflows", "w", null, NodeValue.FromObject(42))];
        Assert.Equal(EOutcome.Succeeded, (await repository.PrepareAsync(original, mutations)).Outcome);
        ReplicationStartResult started = await ReplicationStart.BeginAsync(repository, conflicting, mutations, Metadata(conflicting, mutations));
        Assert.Equal(EOutcome.Succeeded, started.Initialized.Outcome);
        Assert.Equal("Persistence.VersionConflict", started.Prepared?.Reason);
        Assert.Null(await repository.ReadPreparationAsync(conflicting));
        Assert.NotNull(await repository.ReadPreparationAsync(original));
        Assert.Equal(EOutcome.Succeeded, (await repository.CommitAsync(original)).Outcome);
        Assert.Equal(42, (await repository.ReadAsync("workflows", "w"))!.Value!.Json.GetInt32());
    }

    /// <summary>JSONや用途を分けた構成は通常の準備へ戻し、元の保存先で確定します。</summary>
    /// <returns>別媒体を一取引と偽らない結果です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OtherStorageConfigurationsKeepTheirExistingPreparationPath(bool split)
    {
        using MediaTestFiles files = new();
        PersistenceOptions options = new(EStorageBackend.Json);
        if (split)
        {
            options = new(EStorageBackend.Sqlite, new Dictionary<EStoragePurpose, EStorageBackend> { [EStoragePurpose.Configuration] = EStorageBackend.Json });
        }
        IDurableRepository repository = await RepositoryFactory.CreateAsync(files.Root, "a", options).ConfigureAwait(true);
        Guid id = Guid.NewGuid();
        DurableMutation[] mutations = [new("workflows", "w", null, NodeValue.FromObject(42))];
        ReplicationStartResult started = await ReplicationStart.BeginAsync(repository, id, mutations, Metadata(id, mutations));
        Assert.Equal(EOutcome.Succeeded, started.Initialized.Outcome);
        Assert.Null(started.Prepared);
        Assert.Null(await repository.ReadPreparationAsync(id));
        Assert.Equal(EOutcome.Succeeded, (await repository.PrepareAsync(id, started.Mutations)).Outcome);
        IDurableRepository restarted = await RepositoryFactory.CreateAsync(files.Root, "a", options).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, (await restarted.CommitAsync(id)).Outcome);
        Assert.Equal(42, (await restarted.ReadAsync("workflows", "w"))!.Value!.Json.GetInt32());
    }

    /// <summary>試験だけの保存領域を、同じ設定で再度開きます。</summary>
    /// <returns>単一SQLiteまたは用途振分けを持つ保存先です。</returns>
    private static async Task<IDurableRepository> CreateAsync(string directory, bool routed)
    {
        if (routed)
        {
            return await RepositoryFactory.CreateAsync(directory, "a", new(EStorageBackend.Sqlite)).ConfigureAwait(false);
        }
        return new SqliteRepository(Path.Combine(directory, "state.db"));
    }

    /// <summary>実複製と同じローカル用途の開始記録を用意します。</summary>
    /// <returns>固定参加者と共有結果の初期値です。</returns>
    private static DurableMutation[] Metadata(Guid id, IReadOnlyList<DurableMutation> mutations)
        => [new("cluster:replication-members", id.ToString("N"), null, NodeValue.FromObject(members)),
            new("cluster:operations", id.ToString("N"), null, NodeValue.FromObject(new DurableReceipt(id, RepositoryCausality.Fingerprint(mutations), EOutcome.Unknown, [], "Replication.InProgress")))];

    /// <summary>準備行を書こうとした時点で失敗する、試験用のSQLite制約です。</summary>
    /// <returns>障害注入または解除の完了です。</returns>
    private static async Task SetPreparationFailureAsync(string directory, bool fail)
    {
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "state.db"), Pooling = false }.ToString());
        await connection.OpenAsync().ConfigureAwait(false);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DROP TRIGGER IF EXISTS fail_preparation";
        if (fail)
        {
            command.CommandText = "CREATE TRIGGER fail_preparation BEFORE INSERT ON records WHEN NEW.kind='prepared' BEGIN SELECT RAISE(ABORT,'injected preparation write failure'); END";
        }
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}
