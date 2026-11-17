using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.ManagementTransport;

/// <summary>時計換算が元の指紋・残り時間・実体固定を壊さないことを検証します。</summary>
public sealed class RequestAdmissionClockTests
{
    /// <summary>壊れた時計応答を、延長された受付期限や呼出元の実行基盤例外へ変換しません。</summary>
    /// <returns>配送前に不正データとして拒否することです。</returns>
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

    /// <summary>相手の日時を比較せず、配送待ちも元の受付期間から消費します。</summary>
    /// <returns>元本文と受信側の単調時計による期限です。</returns>
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

    /// <summary>対応未確認は旧方式への切替理由にせず、未対応の応答と区別します。</summary>
    /// <returns>照会不能時に副作用要求を準備しないことです。</returns>
    [Fact]
    public async Task OnlyAnExplicitUnsupportedResponseAllowsLegacyDelivery()
    {
        AdmissionTestClock clock = new();
        RequestClockClient client = new(clock);
        ClusterRequest request = new(Guid.NewGuid(), "fixture", "peer", Guid.NewGuid(), clock.GetUtcNow().AddMinutes(1), NodeValue.Null);
        await Assert.ThrowsAsync<IOException>(() => client.PrepareAsync(request, (_, _) => Task.FromResult(new ClusterResponse(EOutcome.Unknown))));
        Assert.Null(await client.PrepareAsync(request, (_, _) => Task.FromResult(new ClusterResponse(EOutcome.NotExecuted, Reason: "Cluster.InspectionTargetUnavailable"))));
    }

    /// <summary>送信側が時計を戻しても、一度消費した元の開始期間を取り直しません。</summary>
    /// <returns>再送の期限が延びないことです。</returns>
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
}
