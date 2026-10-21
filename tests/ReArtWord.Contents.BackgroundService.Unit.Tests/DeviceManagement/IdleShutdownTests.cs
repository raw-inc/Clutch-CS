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
        Assert.NotNull(IdleShutdownDecision.Episode(policy, true, Activity(now.AddMinutes(-10)), now));
        Assert.Null(IdleShutdownDecision.Episode(policy, false, Activity(now.AddMinutes(-10)), now));
        Assert.Null(IdleShutdownDecision.Episode(policy, true, Activity(now.AddMinutes(-9)), now));
        Assert.Null(IdleShutdownDecision.Episode(policy with { EnabledAt = now }, true, Activity(now.AddHours(-3)), now));
        Assert.Null(IdleShutdownDecision.Episode(policy, null, Activity(now.AddMinutes(-20)), now));
        Assert.Null(IdleShutdownDecision.Episode(policy, true, null, now));
        Assert.Null(IdleShutdownDecision.Episode(policy, true, Activity(now.AddMinutes(1)), now));
        Assert.Null(IdleShutdownDecision.Episode(new(), true, Activity(now.AddHours(-3)), now));
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

        IdleShutdownController first = new(() => policy, () => true, _ => Task.FromResult<DeviceUserActivity?>(Activity(input)), attempts.Add, Execute, TimeProvider.System);
        IdleShutdownController next = new(() => policy, () => false, _ => Task.FromResult<DeviceUserActivity?>(Activity(input)), attempts.Add, Execute, TimeProvider.System);
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
        }, TimeProvider.System);
        Assert.Null(await controller.CheckAsync().ConfigureAwait(true));
        Assert.Equal(0, calls);
    }

    /// <summary>入力時刻と再取得しても変化しない入力IDを用意します。</summary>
    /// <returns>試験対象の入力です。</returns>
    private static DeviceUserActivity Activity(DateTimeOffset time) => new(1, time, time.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));
}
