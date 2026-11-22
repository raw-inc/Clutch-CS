using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Power;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Power;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.DeviceManagement;

/// <summary>実OSを停止せず無操作の時間境界と再実行防止を確認します。</summary>
public sealed class IdleShutdownTests
{
    /// <summary>有効化前の無操作、未確認、未到達、電源供給を区別します。</summary>
    [Fact]
    public void RequiresVerifiedInputAndACompleteWaitingPeriod()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DeviceIdleShutdownPolicy policy = new(600, 1200, Guid.NewGuid(), now.AddMinutes(-20));
        Assert.NotNull(IdleShutdownDecision.Episode(policy, true, Activity(now.AddMinutes(-10)), TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(10)));
        Assert.Null(IdleShutdownDecision.Episode(policy, false, Activity(now.AddMinutes(-10)), TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(10)));
        Assert.Null(IdleShutdownDecision.Episode(policy, true, Activity(now.AddMinutes(-9)), TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(9)));
        Assert.Null(IdleShutdownDecision.Episode(policy with { EnabledAt = now }, true, Activity(now.AddHours(-3)), TimeSpan.Zero, TimeSpan.FromHours(3)));
        Assert.Null(IdleShutdownDecision.Episode(policy, null, Activity(now.AddMinutes(-20)), TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(20)));
        Assert.Null(IdleShutdownDecision.Episode(policy, true, null, TimeSpan.FromMinutes(20), TimeSpan.Zero));
        Assert.Null(IdleShutdownDecision.Episode(policy, true, Activity(now.AddMinutes(1)), TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(-1)));
        Assert.Null(IdleShutdownDecision.Episode(new(), true, Activity(now.AddHours(-3)), TimeSpan.Zero, TimeSpan.FromHours(3)));
    }

    /// <summary>OSの無操作時間が十分でも、設定有効化後の実経過が足りなければ時計補正で停止しません。</summary>
    /// <returns>疑似時計と模擬停止処理での確認です。</returns>
    [Theory]
    [InlineData(-3600)]
    [InlineData(3600)]
    public async Task EnableGraceIsIndependentOfWallClockAndIdleDuration(int correction)
    {
        ManagementTransport.AdmissionTestClock clock = new();
        DeviceIdleShutdownPolicy policy = new(600, 600, Guid.NewGuid(), clock.GetUtcNow());
        DeviceUserActivity sample = new(1, clock.GetUtcNow().AddHours(-2), "session") { IdleFor = TimeSpan.FromHours(2) };
        IdleShutdownController controller = new(() => policy, () => true, _ => Task.FromResult<DeviceUserActivity?>(sample),
            _ => true, _ => Task.FromResult(NodeResult.Success()), clock, _ => null);
        Assert.Null(await controller.CheckAsync());
        clock.Advance(TimeSpan.FromSeconds(1));
        clock.Adjust(TimeSpan.FromSeconds(correction));
        Assert.Null(await controller.CheckAsync());
        clock.Advance(TimeSpan.FromSeconds(599));
        Assert.Equal(EOutcome.Succeeded, (await controller.CheckAsync())?.Outcome);
    }

    /// <summary>WTSの日時しかないセッションは、入力を確認できなかった期間や新しい入力を無操作時間に含めません。</summary>
    /// <returns>全セッションを継続して確認できた期間と、模擬停止結果です。</returns>
    [Fact]
    public async Task MissingSessionOrNewInputRestartsObservedIdlePeriod()
    {
        ManagementTransport.AdmissionTestClock clock = new();
        DeviceIdleShutdownPolicy policy = new(60, 60, Guid.NewGuid(), clock.GetUtcNow());
        DeviceUserActivity initial = new(3, clock.GetUtcNow().AddDays(-1), "session");
        DeviceUserActivity? sample = initial;
        IdleShutdownController controller = new(() => policy, () => true, _ => Task.FromResult<DeviceUserActivity?>(sample), _ => true,
            _ => Task.FromResult(NodeResult.Success()), clock, _ => TimeSpan.FromHours(2));
        Assert.Null(await controller.CheckAsync());
        clock.Advance(TimeSpan.FromSeconds(59));
        sample = null;
        Assert.Null(await controller.CheckAsync());
        sample = initial;
        Assert.Null(await controller.CheckAsync());
        clock.Advance(TimeSpan.FromSeconds(59));
        sample = initial with { InputSample = "changed" };
        Assert.Null(await controller.CheckAsync());
        clock.Adjust(TimeSpan.FromHours(1));
        Assert.Null(await controller.CheckAsync());
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(EOutcome.Succeeded, (await controller.CheckAsync())?.Outcome);
    }

    /// <summary>別Coreの受付と再起動後の監視でも、結果不明の同じ停止を再送しません。</summary>
    /// <returns>共有した開始記録の検証です。</returns>
    [Fact]
    public async Task DoesNotRetryAnUnknownShutdownAcrossControllersAsync()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow, input = now.AddMinutes(-20);
        DeviceIdleShutdownPolicy policy = new(60, 60, Guid.NewGuid(), now.AddMinutes(-30));
        HashSet<string> attempts = [];
        int calls = 0;

        // 結果不明を返すOS停止の試験境界です。
        Task<NodeResult> Execute(CancellationToken token)
        {
            calls++;
            return Task.FromResult(NodeResult.Failure("unknown", "unknown", EOutcome.Unknown));
        }

        IdleShutdownController first = new(() => policy, () => true, _ => Task.FromResult<DeviceUserActivity?>(Activity(input)), attempts.Add, Execute, TimeProvider.System, _ => TimeSpan.FromMinutes(30));
        IdleShutdownController next = new(() => policy, () => false, _ => Task.FromResult<DeviceUserActivity?>(Activity(input)), attempts.Add, Execute, TimeProvider.System, _ => TimeSpan.FromMinutes(30));
        Assert.Equal(EOutcome.Unknown, (await first.CheckAsync().ConfigureAwait(true))?.Outcome);
        Assert.Null(await next.CheckAsync().ConfigureAwait(true));
        Assert.Null(await first.CheckAsync().ConfigureAwait(true));
        Assert.Equal(1, calls);
        input = now.AddMinutes(-5);
        Assert.NotNull(await next.CheckAsync().ConfigureAwait(true));
        Assert.Equal(2, calls);
    }

    /// <summary>開始記録直後の新しい入力では実行しません。</summary>
    /// <returns>操作境界での入力再確認です。</returns>
    [Fact]
    public async Task CancelsWhenInputArrivesAfterThePersistentClaimAsync()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow, input = now.AddMinutes(-20);
        DeviceIdleShutdownPolicy policy = new(60, 60, Guid.NewGuid(), now.AddMinutes(-30));
        int calls = 0;
        IdleShutdownController controller = new(() => policy, () => true, _ => Task.FromResult<DeviceUserActivity?>(Activity(input)), _ =>
        {
            input = now;
            return true;
        }, _ =>
        {
            calls++;
            return Task.FromResult(NodeResult.Success());
        }, TimeProvider.System, _ => TimeSpan.FromMinutes(30));
        Assert.Null(await controller.CheckAsync().ConfigureAwait(true));
        Assert.Equal(0, calls);
    }

    /// <summary>入力時刻と再取得しても変化しない入力IDを用意します。</summary>
    /// <returns>試験対象の入力です。</returns>
    private static DeviceUserActivity Activity(DateTimeOffset time) => new(1, time, time.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)) { IdleFor = DateTimeOffset.UtcNow - time };
}
