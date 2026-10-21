using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.WakeOnLan;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.WakeOnLan;
using ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.DeviceManagement;

/// <summary>レジストリを書き換えず、既定選択・版・キャッシュ・明示変更を検証します。</summary>
public sealed class WakeSettingsTests
{
    /// <summary>物理NICが既定で選択され、空の明示選択と選択変更を直ちに反映します。</summary>
    /// <returns>一覧だけがキャッシュされることの確認です。</returns>
    [Fact]
    public async Task SelectionIsFreshWhileInventoryIsReused()
    {
        string physical = Guid.NewGuid().ToString(), virtualId = Guid.NewGuid().ToString();
        DeviceWakeInventory inventory = new([new(physical, "Ethernet", "02-11-22-33-44-55", true), new(virtualId, "Virtual", "02-AA-BB-CC-DD-EE", false)], []);
        DeviceWakeSelection selection = new(true, []);
        WakeClock clock = new();
        int reads = 0, writes = 0;
        using WakeSettings settings = new(_ =>
        {
            reads++;
            return Task.FromResult(inventory);
        }, () => selection, value =>
        {
            writes++;
            selection = value;
        }, clock);
        DeviceSnapshot initial = await settings.ReadAsync();
        DeviceWakeState first = DeviceSnapshots.Read<DeviceWakeState>(initial.Data);
        Assert.Equal([physical], first.Selected().Select(item => item.Id));
        DeviceWakeSelection none = new(false, []);
        DeviceChangeRequest change = new(Guid.NewGuid(), "test-host", Guid.NewGuid(), EDeviceAction.WakeOnLan, new(EDeviceArea.WakeOnLan), initial.Revision, ContractJson.ToValue(none).Json);
        DeviceChangeRules.Validate(change);
        Assert.Equal(EOutcome.Succeeded, settings.Apply(change, initial).Outcome);
        Assert.Equal(1, writes);
        DeviceSnapshot after = await settings.ReadAsync();
        Assert.Empty(DeviceSnapshots.Read<DeviceWakeState>(after.Data).Selected());
        Assert.True(DeviceChangeConfirmation.Matches(change, after));
        Assert.False(DeviceChangeConfirmation.Matches(change, initial));
        Assert.Equal(1, reads);
        selection = new(false, [virtualId]);
        Assert.Equal([virtualId], DeviceSnapshots.Read<DeviceWakeState>((await settings.ReadAsync()).Data).Selected().Select(item => item.Id));
        Assert.Equal(1, reads);
        clock.Advance(TimeSpan.FromMinutes(5));
        _ = await settings.ReadAsync();
        Assert.Equal(2, reads);
        settings.Invalidate();
        _ = await settings.ReadAsync();
        Assert.Equal(3, reads);
        DeviceChangeRequest missing = change with { Change = ContractJson.ToValue(new DeviceWakeSelection(false, [Guid.NewGuid().ToString()])).Json };
        Assert.Equal(EOutcome.NotExecuted, settings.Apply(missing, initial).Outcome);
        Assert.Equal(1, writes);
    }

    /// <summary>自動選択との混在、不正ID、重複したIDは拒否します。</summary>
    [Fact]
    public void InvalidSelectionsCannotBeSaved()
    {
        string id = Guid.NewGuid().ToString();
        Assert.Throws<ArgumentException>(() => new DeviceWakeSelection(true, [id]).Validate());
        Assert.Throws<ArgumentException>(() => new DeviceWakeSelection(false, ["missing"]).Validate());
        Assert.Throws<ArgumentException>(() => new DeviceWakeSelection(false, [id, id.ToUpperInvariant()]).Validate());
        new DeviceWakeSelection(false, []).Validate();
    }

    /// <summary>取得中の再読取り要求を失わず、並行する照会では同じOS列挙を再利用します。</summary>
    /// <returns>期限を待たずに、必要な分だけ再取得することです。</returns>
    [Fact]
    public async Task InvalidatingDuringReadRequiresOneMoreInventoryRead()
    {
        DeviceWakeInventory inventory = new([], []);
        TaskCompletionSource<DeviceWakeInventory> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        using WakeSettings settings = new(_ =>
        {
            reads++;
            if (reads == 1)
            {
                return pending.Task;
            }
            return Task.FromResult(inventory);
        }, () => new(true, []), _ => Assert.Fail("Reading settings must not write."), TimeProvider.System);
        Task<DeviceSnapshot> first = settings.ReadAsync();
        settings.Invalidate();
        pending.SetResult(inventory);
        _ = await first.ConfigureAwait(true);
        DeviceSnapshot[] snapshots = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => settings.ReadAsync()));
        Assert.Equal(2, reads);
        Assert.Single(snapshots.Select(item => item.Revision).Distinct());
    }

    /// <summary>キャッシュ期限だけを進める試験用時刻です。</summary>
    private sealed class WakeClock : TimeProvider
    {
        private DateTimeOffset current = DateTimeOffset.UtcNow;

        /// <summary>現在の試験時刻です。</summary>
        /// <returns>固定したUTC時刻です。</returns>
        public override DateTimeOffset GetUtcNow() => current;

        /// <summary>待機せずに期限を進めます。</summary>
        internal void Advance(TimeSpan elapsed) => current += elapsed;
    }
}
