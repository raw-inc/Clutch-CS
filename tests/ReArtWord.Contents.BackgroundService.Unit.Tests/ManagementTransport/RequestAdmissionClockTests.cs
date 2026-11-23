using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.ManagementTransport;

/// <summary>期限を換算しても、要求内容のハッシュ、残り時間、送信先Coreの実体IDが保たれることを検証します。</summary>
public sealed class RequestAdmissionClockTests
{
    /// <summary>範囲外の時計応答を、算術オーバーフローではなく不正データとして拒否することを確認します。</summary>
    /// <returns>要求を送信する前に不正データを拒否することを検証するタスクです。</returns>
    [Theory]
    [InlineData(-1)]
    [InlineData(long.MaxValue)]
    public async Task MalformedClockCannotPrepareAnExecution(long elapsedTicks)
    {
        AdmissionTestClock clock = new();
        RequestClockClient client = new(clock);
        Guid instance = Guid.NewGuid();
        ClusterRequest request = new(Guid.NewGuid(), "fixture", "peer", instance, clock.GetUtcNow().AddMinutes(1), NodeValue.Null);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.PrepareAsync(request, (_, _) =>
            Task.FromResult(new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(new RequestClockSnapshot(instance, elapsedTicks))))));
    }

    /// <summary>PC間の時計差に影響されず、通信待ちの時間だけ要求の残り時間が減ることを確認します。</summary>
    /// <returns>要求本文を保った期限換算を検証するタスクです。</returns>
    [Theory]
    [InlineData(-3600)]
    [InlineData(3600)]
    public async Task TransitAndWallClockChangesDoNotExtendAdmission(int offsetSeconds)
    {
        AdmissionTestClock sourceClock = new(), targetClock = new();
        targetClock.Adjust(TimeSpan.FromSeconds(offsetSeconds));
        Guid instance = Guid.NewGuid();
        RequestAdmissionClock server = new(instance, targetClock);
        RequestClockClient client = new(sourceClock);
        ClusterRequest original = new(Guid.CreateVersion7(sourceClock.GetUtcNow()), "fixture", "peer", instance, sourceClock.GetUtcNow().AddSeconds(5), NodeValue.FromObject(42));
        string fingerprint = ContractJson.Hash(original);
        Task<ClusterResponse> ProbeAsync(ClusterRequest _, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            RequestClockSnapshot snapshot = server.Capture();
            sourceClock.Advance(TimeSpan.FromSeconds(2));
            targetClock.Advance(TimeSpan.FromSeconds(2));
            return Task.FromResult(new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(snapshot)));
        }
        ClockedClusterRequest envelope = (await client.PrepareAsync(original, ProbeAsync))!;
        ClusterRequest accepted = server.Accept(envelope);
        Assert.Equal(fingerprint, ContractJson.Hash(accepted));
        Assert.Equal(original.ExpiresAt, accepted.ExpiresAt);
        Assert.NotNull(accepted.Admission);
        Assert.Equal(TimeSpan.FromSeconds(1), accepted.Admission.Remaining);
        targetClock.Adjust(TimeSpan.FromHours(-2));
        targetClock.Advance(TimeSpan.FromSeconds(1));
        sourceClock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(accepted.Admission.IsExpired);
        Assert.True(server.Accept(envelope).Admission!.IsExpired);
        ClockedClusterRequest repeated = (await client.PrepareAsync(original, ProbeAsync))!;
        Assert.Equal(envelope.StartByTicks, repeated.StartByTicks);
        Assert.True(server.Accept(repeated).Admission!.IsExpired);
        Assert.Equal(fingerprint, ContractJson.Hash(ContractJson.Copy(accepted)));
        RequestAdmissionClock restarted = new(Guid.NewGuid(), targetClock);
        Assert.Throws<InvalidDataException>(() => restarted.Accept(envelope));
    }

    /// <summary>照会への未応答や不正な応答を、旧形式へ切り替える理由にしないことを確認します。</summary>
    /// <returns>対応を確認できないCoreへ実行要求を送らないことを検証するタスクです。</returns>
    [Fact]
    public async Task OnlyAnExplicitUnsupportedResponseAllowsLegacyDelivery()
    {
        AdmissionTestClock clock = new();
        RequestClockClient client = new(clock);
        ClusterRequest request = new(Guid.NewGuid(), "fixture", "peer", Guid.NewGuid(), clock.GetUtcNow().AddMinutes(1), NodeValue.Null);
        await Assert.ThrowsAsync<IOException>(() => client.PrepareAsync(request, (_, _) => Task.FromResult(new ClusterResponse(EOutcome.Unknown))));
        Assert.Null(await client.PrepareAsync(request, (_, _) => Task.FromResult(new ClusterResponse(EOutcome.NotExecuted, Reason: "Cluster.InspectionTargetUnavailable"))));
    }

    /// <summary>送信側のUTC日時を戻しても、再送時の受付期限が延びないことを確認します。</summary>
    /// <returns>送信側の時刻補正を検証するタスクです。</returns>
    [Fact]
    public async Task SenderClockRollbackDoesNotReviveAnExpiredRequest()
    {
        AdmissionTestClock source = new(), target = new();
        Guid instance = Guid.NewGuid();
        RequestAdmissionClock server = new(instance, target);
        RequestClockClient client = new(source);
        ClusterRequest request = new(Guid.NewGuid(), "fixture", "peer", instance, source.GetUtcNow().AddSeconds(5), NodeValue.Null);
        Task<ClusterResponse> ProbeAsync(ClusterRequest _, CancellationToken token)
            => Task.FromResult(new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(server.Capture())));
        ClockedClusterRequest original = (await client.PrepareAsync(request, ProbeAsync))!;
        source.Advance(TimeSpan.FromSeconds(6));
        target.Advance(TimeSpan.FromSeconds(6));
        source.Adjust(TimeSpan.FromHours(-1));
        ClockedClusterRequest repeated = (await client.PrepareAsync(request, ProbeAsync))!;
        Assert.Equal(original.StartByTicks, repeated.StartByTicks);
        Assert.True(server.Accept(repeated).Admission!.IsExpired);
    }

    /// <summary>時計の進み方に小さな差があっても、送信元の残り時間を超えて受け付けません。</summary>
    [Theory]
    [InlineData(1, true)]
    [InlineData(60, true)]
    [InlineData(61, false)]
    public void ReceiverBoundsTheBudgetWhenTheClockEstimateLeads(int leadMilliseconds, bool accepted)
    {
        AdmissionTestClock clock = new();
        Guid instance = Guid.NewGuid();
        RequestAdmissionClock server = new(instance, clock);
        DateTimeOffset now = clock.GetUtcNow();
        TimeSpan budget = TimeSpan.FromSeconds(5);
        ClusterRequest request = new(Guid.NewGuid(), "fixture", "peer", instance, now + budget, NodeValue.Null);
        ClockedClusterRequest envelope = new(request, instance, (budget + TimeSpan.FromMilliseconds(leadMilliseconds)).Ticks, now);
        if (accepted)
        {
            Assert.Equal(budget, server.Accept(envelope).Admission!.Remaining);
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => server.Accept(envelope));
        }
    }
}
