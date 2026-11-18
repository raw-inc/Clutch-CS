using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Retention;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Requests;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.Unit.Tests.ManagementTransport;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Cluster;

/// <summary>有限期限の実行要求と、永続保存先の旧日時境界を混同しません。</summary>
public sealed class RuntimeReceiptRetirementTests
{
    /// <summary>旧ハッシュの先頭がUUIDv7に見えても、通常の永続要求へ実行用の6分制限を適用しません。</summary>
    /// <returns>旧形式の派生IDと元の結果を維持した受付です。</returns>
    [Fact]
    public async Task LegacyDerivedIdentityIsNotAnImplicitRuntimeDeadlineAsync()
    {
        using MediaTestFiles files = new();
        SqliteRepository repository = new(Path.Combine(files.Root, "state.db"));
        Guid shape = Guid.CreateVersion7(DateTimeOffset.UtcNow.AddDays(10));
        Guid id = DerivedOperationIdentity.FromHash(shape.ToByteArray(), EDerivedIdFormat.Legacy);
        Assert.Equal(7, id.Version);
        RequestExecutor executor = new(repository, "p", TimeProvider.System);
        ClusterRequest request = ClusterRequest.Start(id, "fixture", "p", Guid.NewGuid(), TimeSpan.FromMinutes(5), NodeValue.FromObject(42));
        int effects = 0;
        Task<ClusterResponse> Execute(ClusterRequest _, CancellationToken token)
        {
            effects++;
            return Task.FromResult(new ClusterResponse(EOutcome.Succeeded));
        }
        Assert.Equal(EOutcome.Succeeded, (await executor.ExecuteAsync(request, Execute, true, true).ConfigureAwait(true)).Outcome);
        Assert.Equal(EOutcome.Succeeded, (await executor.ExecuteAsync(request, Execute, true, true).ConfigureAwait(true)).Outcome);
        Assert.NotNull(await repository.ReadAsync("receipts:p", id.ToString("N")).ConfigureAwait(true));
        Assert.Equal(1, effects);
        await executor.DrainAsync().ConfigureAwait(true);
    }

    /// <summary>本文の受付IDと外側のIDが異なる場合は、ノード本体を呼び出しません。</summary>
    /// <returns>明示したIDの取り違えを、別要求として実行しない確認です。</returns>
    [Fact]
    public async Task MismatchedNodeEnvelopeDoesNotExecuteItsBodyAsync()
    {
        using MediaTestFiles files = new();
        SqliteRepository repository = new(Path.Combine(files.Root, "state.db"));
        RequestExecutor executor = new(repository, "p", TimeProvider.System);
        RequestAdmission admission = RequestAdmission.Start(TimeProvider.System, TimeSpan.FromMinutes(5));
        RemoteExecutionRequest node = new(OperationIdentity.CreateExecution(), Guid.NewGuid(), "fixture", "revision", new("node", "test", NodeSettings.Schema("{}")),
            new Dictionary<string, NodeValue>(), "p", Guid.NewGuid(), admission.ExpiresAt, ReceiptId: Guid.CreateVersion7(admission.SourceNow));
        int effects = 0;
        executor.RemoteExecutionHandler = (_, _) =>
        {
            effects++;
            return Task.FromResult(NodeResult.Success());
        };
        ClusterRequest wrong = new(node.Id, "node.execute", "p", node.TargetInstanceId, node.ExpiresAt, NodeValue.FromObject(node)) { Admission = admission };
        ClusterResponse refused = await executor.ExecuteAsync(wrong, executor.InvokeRegisteredAsync, true, true).ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, refused.Outcome);
        Assert.Equal("Execution.RequestIdentityMismatch", refused.Reason);
        Assert.Equal(0, effects);
        Assert.Equal(EOutcome.Succeeded, (await executor.ExecuteAsync(wrong with { Id = node.DeliveryId }, executor.InvokeRegisteredAsync, true, true).ConfigureAwait(true)).Outcome);
        Assert.Equal(1, effects);
        await executor.DrainAsync().ConfigureAwait(true);
    }

    /// <summary>遅いPCから届いた新規実行は、旧日時境界があっても結果を保存し、照会から返します。</summary>
    /// <returns>実際の媒体と受付期限換算を通した結果保存の確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClockedRuntimeResultIsPersistedAcrossALegacyBoundaryAsync(bool json)
    {
        using MediaTestFiles files = new();
        IDurableRepository repository = json ? new JsonFileRepository(Path.Combine(files.Root, "state.json"))
                                             : new SqliteRepository(Path.Combine(files.Root, "state.db"));
        AdmissionTestClock source = new(), target = new();
        source.Adjust(TimeSpan.FromDays(-31));
        Guid instance = Guid.NewGuid();
        RequestAdmissionClock server = new(instance, target);
        RequestClockClient client = new(source);
        HistoryRetentionBoundary boundary = new(new Dictionary<ERetentionCategory, DateTimeOffset>(), new Dictionary<ERetentionCategory, DateTimeOffset>(),
            target.GetUtcNow().AddDays(-30), target.GetUtcNow());
        await repository.WriteLocalSnapshotAsync(RepositoryHistoryGuard.Scope, "boundary", NodeValue.FromObject(boundary)).ConfigureAwait(true);
        ClusterRequest original = ClusterRequest.Start("fixture", "p", instance, TimeSpan.FromMinutes(5), NodeValue.FromObject(42), source);
        Task<ClusterResponse> Probe(ClusterRequest _, CancellationToken token) => Task.FromResult(new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(server.Capture())));
        ClusterRequest accepted = server.Accept((await client.PrepareAsync(original, Probe).ConfigureAwait(true))!);
        RuntimeRequestExecutor executor = new(repository, "receipts:p", target);
        int effects = 0;
        Task<ClusterResponse> Execute(ClusterRequest _, CancellationToken token)
        {
            effects++;
            return Task.FromResult(new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(42)));
        }
        try
        {
            Assert.Equal(EOutcome.Succeeded, (await executor.ExecuteAsync(accepted, Execute, true, true).ConfigureAwait(true)).Outcome);
        }
        finally
        {
            await executor.DrainAsync().ConfigureAwait(true);
        }
        RuntimeRequestExecutor reopened = new(repository, "receipts:p", target);
        ClusterResponse persisted = await reopened.ExecuteAsync(accepted, Execute, false, true).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, persisted.Outcome);
        RequestExecutor query = new(repository, "p", target);
        Assert.Equal(EOutcome.Succeeded, (await query.ReadStatusAsync(new(original.Id, ContractJson.Hash(original))).ConfigureAwait(true)).Response?.Outcome);
        Assert.Equal(1, effects);
    }

    /// <summary>結果は受信側の保持期間で整理し、整理後に期限や保存方式を変えた再送も開始しません。</summary>
    /// <returns>時計差、同期・非同期記録、JSON・SQLiteでの保持量と再実行防止です。</returns>
    [Theory]
    [InlineData(false, -31, false)]
    [InlineData(false, -31, true)]
    [InlineData(false, 31, false)]
    [InlineData(false, 31, true)]
    [InlineData(true, -31, false)]
    [InlineData(true, -31, true)]
    [InlineData(true, 31, false)]
    [InlineData(true, 31, true)]
    public async Task LocalRetentionDoesNotPermitReplayThroughAnotherReceiptModeAsync(bool json, int offsetDays, bool buffered)
    {
        using MediaTestFiles files = new();
        IDurableRepository repository = json ? new JsonFileRepository(Path.Combine(files.Root, "state.json"))
                                             : new SqliteRepository(Path.Combine(files.Root, "state.db"));
        AdmissionTestClock source = new(), target = new();
        target.Adjust(DateTimeOffset.UtcNow - target.GetUtcNow());
        source.Adjust(target.GetUtcNow() - source.GetUtcNow() + TimeSpan.FromDays(offsetDays));
        Guid instance = Guid.NewGuid();
        RequestAdmissionClock server = new(instance, target);
        RequestClockClient client = new(source);
        Task<ClusterResponse> Probe(ClusterRequest _, CancellationToken token) => Task.FromResult(new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(server.Capture())));
        int effects = 0;
        Task<ClusterResponse> Execute(ClusterRequest _, CancellationToken token)
        {
            effects++;
            return Task.FromResult(new ClusterResponse(EOutcome.Succeeded));
        }
        using HistoryArchiveService archive = new(repository);
        await archive.MaintainAsync(new(), new(1, 3), target.GetUtcNow()).ConfigureAwait(true);
        RequestAdmission admission = RequestAdmission.Start(source, TimeSpan.FromMinutes(5));
        RemoteExecutionRequest node = new(OperationIdentity.CreateExecution(), Guid.NewGuid(), "fixture", "revision", new("node", "test", NodeSettings.Schema("{}")),
            new Dictionary<string, NodeValue>(), "p", instance, admission.ExpiresAt, BufferedReceipt: buffered, ReceiptId: Guid.CreateVersion7(admission.SourceNow));
        ClusterRequest original = new(node.DeliveryId, "node.execute", "p", instance, admission.ExpiresAt, NodeValue.FromObject(node)) { Admission = admission };
        ClockedClusterRequest originalEnvelope = (await client.PrepareAsync(original, Probe).ConfigureAwait(true))!;
        ClusterRequest accepted = server.Accept(originalEnvelope);
        RequestExecutor executor = new(repository, "p", target);
        try
        {
            Assert.Equal(EOutcome.Succeeded, (await executor.ExecuteAsync(accepted, Execute, true, true).ConfigureAwait(true)).Outcome);
        }
        finally
        {
            await executor.DrainAsync().ConfigureAwait(true);
        }
        string scope = Infrastructure.Persistence.Receipts.BoundedRequestReceipts.Select("receipts:p", accepted, bounded: true);
        string key = accepted.Id.ToString("N");
        Assert.NotNull(await repository.ReadAsync(scope, key).ConfigureAwait(true));
        source.Advance(TimeSpan.FromDays(5));
        target.Advance(TimeSpan.FromDays(5));
        for (int pass = 0; pass < 8; pass++)
        {
            await archive.MaintainAsync(new(), new(1, 3), target.GetUtcNow()).ConfigureAwait(true);
        }
        Assert.Null(await repository.ReadAsync(scope, key).ConfigureAwait(true));
        Assert.False(await repository.IsHistoryRetiredAsync(scope, key).ConfigureAwait(true));
        foreach (bool runtimeMode in new[] { true, false })
        {
            RequestAdmission extension = RequestAdmission.Start(source, TimeSpan.FromMinutes(5));
            ClusterRequest extended = original with { ExpiresAt = extension.ExpiresAt, Admission = extension,
                Payload = NodeValue.FromObject(node with { BufferedReceipt = runtimeMode, ExpiresAt = extension.ExpiresAt }) };
            ClusterRequest replay = server.Accept((await client.PrepareAsync(extended, Probe).ConfigureAwait(true))!);
            RequestExecutor retried = new(repository, "p", target);
            try
            {
                ClusterResponse refused = await retried.ExecuteAsync(replay, Execute, true, true).ConfigureAwait(true);
                Assert.Equal(EOutcome.NotExecuted, refused.Outcome);
                Assert.Equal("Execution.RuntimeDeadlineInvalid", refused.Reason);
            }
            finally
            {
                await retried.DrainAsync().ConfigureAwait(true);
            }
        }
        source.Adjust(TimeSpan.FromDays(-5));
        target.Adjust(TimeSpan.FromHours(-4));
        ClockedClusterRequest restoredEnvelope = System.Text.Json.JsonSerializer.Deserialize<ClockedClusterRequest>(System.Text.Json.JsonSerializer.Serialize(originalEnvelope))!;
        ClusterRequest lateOriginal = server.Accept(restoredEnvelope);
        Assert.True(lateOriginal.Admission!.IsExpired);
        RequestExecutor corrected = new(repository, "p", target);
        ClusterResponse late = await corrected.ExecuteAsync(lateOriginal, Execute, true, true).ConfigureAwait(true);
        if (buffered)
        {
            Assert.Equal(EOutcome.Unknown, late.Outcome);
            Assert.Equal("Execution.BufferedReceiptUnconfirmed", late.Reason);
        }
        else
        {
            Assert.Equal(EOutcome.NotExecuted, late.Outcome);
            Assert.Equal("Execution.Expired", late.Reason);
        }
        Assert.Equal(1, effects);
    }
}
