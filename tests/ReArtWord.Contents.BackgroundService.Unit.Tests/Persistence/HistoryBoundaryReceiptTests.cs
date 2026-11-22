using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Retention;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>失効境界しか残っていない要求を、内容が異なると断定しません。</summary>
public sealed class HistoryBoundaryReceiptTests
{
    /// <summary>時計が遅い新規IDと退役済みIDを区別できない間は、Apply・Prepare・解消のどれも再実行しません。</summary>
    /// <returns>JSON・SQLiteと用途別振分けを通した確認不能の保持です。</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task UnknownContentIsNotReportedAsAnOperationMismatchAsync(int backend)
    {
        using MediaTestFiles files = new();
        IDurableRepository repository;
        if (backend < 2)
        {
            repository = backend == 0 ? new JsonFileRepository(Path.Combine(files.Root, "state.json"))
                                      : new SqliteRepository(Path.Combine(files.Root, "state.db"));
        }
        else
        {
            EStorageBackend kind = backend == 2 ? EStorageBackend.Json
                                                : EStorageBackend.Sqlite;
            repository = await RepositoryFactory.CreateAsync(files.Root, "peer", new(kind)).ConfigureAwait(true);
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        HistoryRetentionBoundary boundary = new(new Dictionary<ERetentionCategory, DateTimeOffset>(), new Dictionary<ERetentionCategory, DateTimeOffset>(), now.AddDays(-30), now);
        await repository.WriteLocalSnapshotAsync(RepositoryHistoryGuard.Scope, "boundary", NodeValue.FromObject(boundary)).ConfigureAwait(true);
        Guid uncertain = Guid.CreateVersion7(now.AddDays(-31));
        DurableMutation[] mutations = [new("workflow:clock-boundary", "value", null, NodeValue.FromObject(1))];
        AssertUnknown(await repository.ApplyBatchAsync(uncertain, mutations).ConfigureAwait(true));
        AssertUnknown(await repository.PrepareAsync(uncertain, mutations).ConfigureAwait(true));
        AssertUnknown(await repository.ResolveConflictAsync(uncertain, "workflow:clock-boundary", "value", [], NodeValue.FromObject(2)).ConfigureAwait(true));
        AssertUnknown((await repository.ReadReceiptAsync(uncertain).ConfigureAwait(true))!);
        Assert.Null(await repository.ReadAsync("workflow:clock-boundary", "value").ConfigureAwait(true));
        Guid current = Guid.NewGuid();
        Assert.Equal(EOutcome.Succeeded, (await repository.ApplyBatchAsync(current, mutations).ConfigureAwait(true)).Outcome);
        DurableReceipt mismatch = await repository.ApplyBatchAsync(current, [mutations[0] with { Value = NodeValue.FromObject(2) }]).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, mismatch.Outcome);
        Assert.Equal("Persistence.OperationMismatch", mismatch.Reason);
        Assert.Equal(1, (await repository.ReadAsync("workflow:clock-boundary", "value").ConfigureAwait(true))!.Value!.Json.GetInt32());
    }

    /// <summary>失われた内容を一致や不一致として推測せず、Unknownを維持します。</summary>
    private static void AssertUnknown(DurableReceipt result)
    {
        Assert.Equal(EOutcome.Unknown, result.Outcome);
        Assert.Equal(RepositoryHistoryGuard.ExpiredReason, result.Reason);
    }
}
