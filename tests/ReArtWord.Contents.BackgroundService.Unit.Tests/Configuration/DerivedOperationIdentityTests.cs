using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Requests;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Configuration;

/// <summary>派生IDを発行日時と誤認せず、旧操作のIDと退役済みの受付を維持することを確認します。</summary>
public sealed class DerivedOperationIdentityTests
{
    /// <summary>UUIDv8の要求も永続受付で重複を防ぎ、実行器の再生成後に元の結果を返します。</summary>
    /// <returns>時刻を持たないIDに対する実受付と重複実行防止です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DurableRequestReplayKeepsOneEffectAfterExecutorRestartAsync(bool json)
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "runtime");
        IDurableRepository repository = Create(path, json);
        Guid id;
        using (DerivedIdentityScope identities = new(EDerivedIdFormat.Uuid8))
        {
            id = WorkflowStore.PhaseId(Guid.Parse("00000000-0000-0000-0000-00000000025f"), "snapshot");
        }
        ClusterRequest request = ClusterRequest.Start(id, "fixture", "peer", Guid.NewGuid(), TimeSpan.FromMinutes(1), NodeValue.Null);
        Assert.False(RuntimeRequestIdentity.TryDeadline(request, DateTimeOffset.UtcNow, out _));
        int effects = 0;
        Task<ClusterResponse> ExecuteAsync(ClusterRequest _, CancellationToken token)
        {
            effects++;
            return Task.FromResult(new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(42)));
        }
        RequestExecutor first = new(repository, "peer", TimeProvider.System);
        Assert.Equal(EOutcome.Succeeded, (await first.ExecuteAsync(request, ExecuteAsync, true, false).ConfigureAwait(true)).Outcome);
        RequestExecutor restarted = new(Create(path, json), "peer", TimeProvider.System);
        Assert.Equal(EOutcome.Succeeded, (await restarted.ExecuteAsync(request, ExecuteAsync, true, false).ConfigureAwait(true)).Outcome);
        Assert.Equal(1, effects);
    }

    /// <summary>実際のハッシュがUUIDv7になる入力でも、新方式には発行時刻を持たせません。</summary>
    [Fact]
    public void NewFormatIsOpaqueAndLegacyFormatKeepsItsExactBytes()
    {
        Guid source = Guid.Parse("00000000-0000-0000-0000-00000000025f");
        Guid legacy = WorkflowStore.PhaseId(source, "snapshot");
        Assert.Equal(Guid.Parse("005ca1b1-1033-778c-ac8e-51522b813554"), legacy);
        Assert.True(OperationIdentity.TryGetIssuedAt(legacy, out DateTimeOffset timestamp));
        Assert.True(timestamp < DateTimeOffset.UnixEpoch.AddYears(30));
        using (DerivedIdentityScope scope = new(EDerivedIdFormat.Uuid8))
        {
            Guid current = WorkflowStore.PhaseId(source, "snapshot");
            Assert.Equal(Guid.Parse("b1a15c00-3310-8c77-ac8e-51522b813554"), current);
            Assert.False(OperationIdentity.TryGetIssuedAt(current, out _));
        }
        Assert.Equal(legacy, WorkflowStore.PhaseId(source, "snapshot"));
    }

    /// <summary>保存媒体を開き直しても、同じ派生IDで結果を照会し、時刻付きIDの保護は残します。</summary>
    /// <returns>SQLiteとJSONでの保存・重複防止・失効境界の確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpaqueDerivedIdSurvivesRestartWithoutRelaxingTimedIdentityRetentionAsync(bool json)
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "derived");
        IDurableRepository repository = Create(path, json);
        HistoryRetentionBoundary boundary = new(new Dictionary<Contracts.Retention.ERetentionCategory, DateTimeOffset>(),
            new Dictionary<Contracts.Retention.ERetentionCategory, DateTimeOffset>(), DateTimeOffset.UnixEpoch.AddYears(30), DateTimeOffset.UtcNow);
        await repository.WriteLocalSnapshotAsync("peer:repository-history", "boundary", NodeValue.FromObject(boundary)).ConfigureAwait(true);
        Guid source = Guid.Parse("00000000-0000-0000-0000-00000000025f");
        Guid legacy = WorkflowStore.PhaseId(source, "snapshot");
        Assert.Equal("Persistence.HistoryExpired", (await repository.ReadReceiptAsync(legacy).ConfigureAwait(true))!.Reason);
        Guid current;
        using (DerivedIdentityScope scope = new(EDerivedIdFormat.Uuid8))
        {
            current = WorkflowStore.PhaseId(source, "snapshot");
        }
        DurableMutation[] changes = [new("workflow:example", "value", null, NodeValue.FromObject("saved"))];
        Assert.Equal(EOutcome.Succeeded, (await repository.ApplyBatchAsync(current, changes).ConfigureAwait(true)).Outcome);
        repository = Create(path, json);
        Assert.Equal(EOutcome.Succeeded, (await repository.ApplyBatchAsync(current, changes).ConfigureAwait(true)).Outcome);
        Assert.Equal("saved", (await repository.ReadAsync("workflow:example", "value").ConfigureAwait(true))!.Value!.GetString());
        Assert.Equal("Persistence.HistoryExpired", (await repository.ReadReceiptAsync(legacy).ConfigureAwait(true))!.Reason);
    }

    /// <summary>新記録と旧記録を並行して再開しても、各操作の生成方式を混ぜません。</summary>
    /// <returns>非同期処理を跨ぐ方式の分離です。</returns>
    [Fact]
    public async Task ParallelOperationsKeepTheirSavedFormatAsync()
    {
        TaskCompletionSource first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource second = new(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task RunAsync(EDerivedIdFormat format, TaskCompletionSource ready, Task other)
        {
            using DerivedIdentityScope scope = new(format);
            ready.SetResult();
            await other.ConfigureAwait(true);
            Assert.Equal(format, DerivedIdentityScope.Current);
        }
        await Task.WhenAll(RunAsync(EDerivedIdFormat.Uuid8, first, second.Task), RunAsync(EDerivedIdFormat.Legacy, second, first.Task)).ConfigureAwait(true);
        Assert.Equal(EDerivedIdFormat.Legacy, DerivedIdentityScope.Current);
    }

    /// <summary>元操作の記録が退役した後は、生成方式を変更しても開始記録を作り直せません。</summary>
    /// <returns>旧操作の消失を新操作の開始条件にしない確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetiredParentCannotBeRestartedWithNewChildIdsAsync(bool json)
    {
        using MediaTestFiles files = new();
        IDurableRepository repository = Create(Path.Combine(files.Root, "retired"), json);
        using HistoryArchiveService archive = new(repository);
        ConfigurationOperationJournal journal = new(repository);
        Guid id = Guid.NewGuid();
        object request = new { value = 42 };
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await archive.MaintainAsync(new(), new(0, 1), now).ConfigureAwait(true);
        Assert.True(await journal.BeginAsync("identity-test", id, request).ConfigureAwait(true));
        await journal.CompleteAsync("identity-test", id, new ConfigurationPhaseResult(EOutcome.Succeeded)).ConfigureAwait(true);
        await archive.MaintainAsync(new(), new(0, 1), now.AddMinutes(20)).ConfigureAwait(true);
        for (int iteration = 0; iteration < 8; iteration++)
        {
            await archive.MaintainAsync(new(), new(0, 1), now.AddDays(3)).ConfigureAwait(true);
        }
        Assert.Null(await journal.ReadAsync("identity-test", id).ConfigureAwait(true));
        using DerivedIdentityScope scope = new(EDerivedIdFormat.Uuid8);
        Assert.False(await journal.BeginAsync("identity-test", id, request).ConfigureAwait(true));
    }

    /// <summary>生成方式を含む保存条件を復元し、旧版が誤って参加集合として使用できないことを確認します。</summary>
    [Fact]
    public void SavedContextPreservesLegacyAndRejectsUnsupportedGeneration()
    {
        string[] names = ["peer-a", "peer-b"];
        JsonElement peers = JsonSerializer.SerializeToElement(names);
        ConfigurationOperation old = new("fingerprint", DateTimeOffset.UtcNow, peers, null);
        Assert.Equal(EDerivedIdFormat.Legacy, ConfigurationIdentityContext.Read(old).IdentityFormat);
        ConfigurationOperation current = old with { Context = ConfigurationIdentityContext.Write(peers, EDerivedIdFormat.Uuid8) };
        Assert.Throws<JsonException>(() => current.Context!.Value.Deserialize<string[]>());
        ConfigurationOperation restored = ConfigurationIdentityContext.Read(current);
        Assert.Equal(EDerivedIdFormat.Uuid8, restored.IdentityFormat);
        Assert.Equal(peers.GetRawText(), restored.Context!.Value.GetRawText());
        ConfigurationOperation future = old with { Context = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["$clutchDerivedIds"] = "future", ["value"] = peers }) };
        Assert.Throws<InvalidDataException>(() => ConfigurationIdentityContext.Read(future));
    }

    /// <summary>同じ試験を、独立した二つの保存実装に適用します。</summary>
    /// <returns>試験専用の保存先です。</returns>
    private static IDurableRepository Create(string path, bool json)
    {
        if (json)
        {
            return new JsonFileRepository(path + ".json");
        }
        return new SqliteRepository(path + ".db");
    }
}
