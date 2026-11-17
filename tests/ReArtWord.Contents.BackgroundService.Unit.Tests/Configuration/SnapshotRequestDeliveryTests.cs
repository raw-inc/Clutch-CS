using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.Unit.Tests.ManagementTransport;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Configuration;

/// <summary>Snapshotの受付Coreが替わっても、初回の要求・実体・期限を維持します。</summary>
public sealed class SnapshotRequestDeliveryTests
{
    /// <summary>準備途中の終了後に、新しい受付期間を付けて元要求を送信しません。</summary>
    /// <returns>同じCoreでの準備再開と、別寿命での送信拒否です。</returns>
    [Fact]
    public async Task InterruptedPreparationKeepsItsOriginalBudgetAndCannotRestartItAsync()
    {
        using MediaTestFiles files = new();
        SqliteRepository repository = new(Path.Combine(files.Root, "pending.db"));
        AdmissionTestClock clock = new();
        Guid target = Guid.NewGuid();
        RequestAdmissionClock receiver = new(target, clock);
        RequestClockClient source = new(clock);
        bool unavailable = true;
        SnapshotRequestDelivery delivery = new(repository, (request, token) =>
        {
            if (unavailable)
            {
                throw new IOException("Clock reply is unavailable.");
            }
            return source.PrepareAsync(request, (_, _) => Task.FromResult(new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(receiver.Capture()))), token);
        });
        string scope = "cluster:snapshot-requests";
        ClusterRequest request = ClusterRequest.Start(Guid.NewGuid(), "snapshot.prepare", "target", target, TimeSpan.FromSeconds(30), NodeValue.Null, clock);
        await Assert.ThrowsAsync<IOException>(() => delivery.PrepareAsync(scope, request)).ConfigureAwait(true);
        Assert.Equal(2, (await repository.ListAsync(scope).ConfigureAwait(true)).Count);
        SnapshotRequestDelivery other = new(repository, (_, _) => throw new InvalidOperationException("A restarted owner must not issue a new deadline."));
        await Assert.ThrowsAsync<InvalidDataException>(() => other.PrepareAsync(scope, request)).ConfigureAwait(true);
        clock.Advance(TimeSpan.FromSeconds(10));
        unavailable = false;
        ClusterRequest resumed = (await delivery.PrepareAsync(scope, request).ConfigureAwait(true))!;
        RequestClockStamp stamp = resumed.DeliveryTiming!;
        Assert.Equal(TimeSpan.FromSeconds(20), receiver.Accept(new(resumed, stamp.InstanceId, stamp.StartByTicks, stamp.SourceNow)).Admission!.Remaining);
        Assert.Equal(stamp, (await other.ReadAsync(scope, request.Id).ConfigureAwait(true))!.DeliveryTiming);
    }

    /// <summary>時計が遅い別Coreからの回復で、元の要求を再換算・延命しません。</summary>
    /// <returns>保存媒体を開き直した後の期限と、旧形式との互換性です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnotherCoreRestoresTheOriginalClockAndExpiredRequestsStayExpiredAsync(bool json)
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "requests");
        IDurableRepository repository = Create(path, json);
        AdmissionTestClock source = new(), target = new(), nextSource = new();
        target.Adjust(TimeSpan.FromHours(1));
        nextSource.Adjust(TimeSpan.FromHours(-1));
        Guid instance = Guid.NewGuid();
        RequestAdmissionClock server = new(instance, target);
        RequestClockClient clock = new(source);
        Task<ClusterResponse> ProbeAsync(ClusterRequest _, CancellationToken token)
            => Task.FromResult(new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(server.Capture())));
        SnapshotRequestDelivery initial = new(repository, (request, token) => clock.PrepareAsync(request, ProbeAsync, token));
        ClusterRequest original = ClusterRequest.Start(Guid.NewGuid(), "snapshot.prepare", "target", instance,
            TimeSpan.FromSeconds(30), NodeValue.FromObject(7), source);
        ClusterRequest saved = (await initial.PrepareAsync("cluster:snapshot-requests", original).ConfigureAwait(true))!;
        Assert.True(saved.DeliveryPrepared);
        Assert.NotNull(saved.DeliveryTiming);
        Assert.Equal(ContractJson.Hash(original), ContractJson.Hash(saved));
        Assert.Equal(2, (await repository.ListAsync("cluster:snapshot-requests").ConfigureAwait(true)).Count);
        target.Advance(TimeSpan.FromSeconds(31));
        RequestClockClient laterClock = new(nextSource);
        ClockedClusterRequest wrong = (await laterClock.PrepareAsync(ContractJson.Copy(original), ProbeAsync).ConfigureAwait(true))!;
        Assert.False(server.Accept(wrong).Admission!.IsExpired);

        // 同じ保存を別の受付Coreが読み直します。期限の再照会が必要になった時点で試験を失敗させます。
        SnapshotRequestDelivery recovery = new(Create(path, json), (_, _) => throw new InvalidOperationException("Recovery must not recalibrate."));
        ClusterRequest restored = (await recovery.PrepareAsync("cluster:snapshot-requests", original with
        { ExpiresAt = nextSource.GetUtcNow().AddSeconds(30), Admission = null }).ConfigureAwait(true))!;
        Assert.Equal(saved.DeliveryTiming, restored.DeliveryTiming);
        Assert.Equal(saved.TargetInstanceId, restored.TargetInstanceId);
        Assert.Equal(saved.ExpiresAt, restored.ExpiresAt);
        Assert.Equal(ContractJson.Hash(saved), ContractJson.Hash(restored));
        RequestClockStamp stamp = restored.DeliveryTiming!;
        ClusterRequest expired = server.Accept(new(restored, stamp.InstanceId, stamp.StartByTicks, stamp.SourceNow));
        Assert.True(expired.Admission!.IsExpired);
        RequestAdmissionClock restarted = new(Guid.NewGuid(), target);
        Assert.Throws<InvalidDataException>(() => restarted.Accept(new(restored, stamp.InstanceId, stamp.StartByTicks, stamp.SourceNow)));
    }

    /// <summary>旧記録と、未対応を確認して保存した新記録を、どちらも後から新形式へ変更しません。</summary>
    /// <returns>準備済みフラグと、元の本文・期限の保持です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyDeliveryDoesNotAcquireAnotherDeadlineAsync(bool existing)
    {
        using MediaTestFiles files = new();
        SqliteRepository repository = new(Path.Combine(files.Root, "state.db"));
        ClusterRequest original = ClusterRequest.Start(Guid.NewGuid(), "snapshot.prepare", "target", Guid.NewGuid(), TimeSpan.FromSeconds(30), NodeValue.Null);
        string scope = "cluster:snapshot-requests";
        if (existing)
        {
            _ = await repository.ApplyBatchAsync(Guid.NewGuid(), [new DurableMutation(scope, original.Id.ToString("N"), null, NodeValue.FromObject(original))]).ConfigureAwait(true);
        }
        int probes = 0;
        SnapshotRequestDelivery initial = new(repository, (_, _) =>
        {
            probes++;
            return Task.FromResult<ClockedClusterRequest?>(null);
        });
        ClusterRequest saved = (await initial.PrepareAsync(scope, original).ConfigureAwait(true))!;
        Assert.Equal(existing, probes == 0);
        Assert.True(saved.DeliveryPrepared);
        Assert.Null(saved.DeliveryTiming);
        SnapshotRequestDelivery restarted = new(repository, (_, _) => throw new InvalidOperationException("Do not upgrade a saved delivery."));
        ClusterRequest restored = (await restarted.ReadAsync(scope, original.Id).ConfigureAwait(true))!;
        Assert.True(restored.DeliveryPrepared);
        Assert.Null(restored.DeliveryTiming);
        Assert.Equal(ContractJson.Hash(original), ContractJson.Hash(restored));
    }

    /// <summary>要求と異なる受付情報を読み替えず、送信前に拒否します。</summary>
    /// <returns>補助記録が損傷した場合の停止境界です。</returns>
    [Fact]
    public async Task MismatchedMetadataCannotBeUsedForRecoveryAsync()
    {
        using MediaTestFiles files = new();
        SqliteRepository repository = new(Path.Combine(files.Root, "state.db"));
        ClusterRequest request = ClusterRequest.Start(Guid.NewGuid(), "snapshot.prepare", "target", Guid.NewGuid(), TimeSpan.FromSeconds(30), NodeValue.Null);
        string scope = "cluster:snapshot-requests", key = request.Id.ToString("N");
        _ = await repository.ApplyBatchAsync(Guid.NewGuid(), [new DurableMutation(scope, key, null, NodeValue.FromObject(request)),
            new DurableMutation(scope, key + ".admission", null, NodeValue.FromObject(new { Version = 1, Fingerprint = "other", Prepared = true, Timing = (RequestClockStamp?)null }))]).ConfigureAwait(true);
        SnapshotRequestDelivery delivery = new(repository, (_, _) => throw new InvalidOperationException("Do not repair metadata by recalibration."));
        await Assert.ThrowsAsync<InvalidDataException>(() => delivery.ReadAsync(scope, request.Id)).ConfigureAwait(true);
    }

    /// <summary>同じ試験を両方の永続形式で確認します。</summary>
    /// <returns>試験専用の保存領域です。</returns>
    private static IDurableRepository Create(string path, bool json)
    {
        if (json)
        {
            return new JsonFileRepository(path + ".json");
        }
        return new SqliteRepository(path + ".db");
    }
}
