using System.ComponentModel;
using System.Runtime.Versioning;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Power;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Power;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.DeviceManagement;

/// <summary>電源操作を実OSへ送らず、部分変更と障害境界を確認します。</summary>
[SupportedOSPlatform("windows")]
public sealed class DevicePowerTests
{
    /// <summary>ACだけの変更でDC・ボタン動作・他の自動タイマーを維持します。</summary>
    [Fact]
    public void ChangesOnlyExplicitValuesAndVerifiesThem()
    {
        Access access = new();
        WindowsPowerSettings platform = new(access);
        DeviceSnapshot before = platform.Read();
        DeviceChangeRequest request = Request(before, new(Ac: new(DisplaySeconds: 0), FastStartup: false, IdleShutdownAcSeconds: 1800));
        DeviceChangeRules.Validate(request);
        DeviceChangeResult result = platform.Apply(request, before);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(["ac.DisplaySeconds", "activate", "startup", "idle"], access.Calls);
        Assert.Equal(600U, access.State.Dc.DisplaySeconds);
        Assert.Equal(3U, access.State.Ac.PowerButtonAction);
        Assert.Equal(0U, access.State.IdleShutdown.DcSeconds);
        Assert.NotEqual(Guid.Empty, access.State.IdleShutdown.Version);
        Assert.True(DeviceChangeConfirmation.Matches(request, platform.Read()));
        Assert.False(DeviceChangeConfirmation.Matches(request, before));
    }

    /// <summary>ポリシー・未対応機能・未確認値・空変更を開始前に拒否します。</summary>
    [Theory]
    [InlineData("policy")]
    [InlineData("hibernate")]
    [InlineData("unavailable")]
    [InlineData("empty")]
    [InlineData("range")]
    [InlineData("changed")]
    public void RejectsInvalidChangesBeforeWriting(string kind)
    {
        Access access = new();
        access.State = kind switch
        {
            "policy" => access.State with { Restrictions = ["SleepSeconds"] },
            "hibernate" => access.State with { HibernateAvailable = false },
            "unavailable" => access.State with { Ac = access.State.Ac with { HibernateSeconds = null } },
            _ => access.State,
        };
        WindowsPowerSettings platform = new(access);
        DeviceSnapshot before = platform.Read();
        DevicePowerChange change = kind switch
        {
            "empty" => new(),
            "range" => new(IdleShutdownAcSeconds: 604801),
            _ => new(Ac: new(HibernateSeconds: 120)),
        };
        string changed = "changed";
        if (string.Equals(kind, changed, StringComparison.Ordinal))
        {
            access.State = access.State with { SchemeId = Guid.NewGuid() };
        }
        Assert.Equal(EOutcome.NotExecuted, platform.Apply(Request(before, change), before).Outcome);
        Assert.Empty(access.Calls);
    }

    /// <summary>途中失敗を全体成功とせず、無操作停止設定を後続として開始しません。</summary>
    [Fact]
    public void PreservesCompletedStepsAndDoesNotEnableShutdownAfterFailure()
    {
        Access access = new() { FailActivate = true };
        WindowsPowerSettings platform = new(access);
        DeviceSnapshot before = platform.Read();
        DeviceChangeResult result = platform.Apply(Request(before, new(Ac: new(DisplaySeconds: 0), IdleShutdownAcSeconds: 60)), before);
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Equal(["power.ac.DisplaySeconds"], result.CompletedSteps);
        Assert.Equal(0U, access.State.IdleShutdown.AcSeconds);
    }

    /// <summary>実Windowsのネイティブ構造体と設定値を読取り専用で確認します。</summary>
    [Fact]
    public void ReadsWindowsWithoutChangingAnyPowerSetting()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        DevicePowerState state = new WindowsPowerAccess().Read();
        Assert.NotEqual(Guid.Empty, state.SchemeId);
        Assert.NotEmpty(state.SchemeName);
        Assert.NotNull(state.Ac.DisplaySeconds);
        Assert.NotNull(WindowsIdleInput.OnAcPower());
        DeviceUserActivity? input = WindowsIdleInput.LastActivity();
        Assert.True(input is null || input.LastActivity <= DateTimeOffset.UtcNow);
        DeviceUserActivity? console = WindowsIdleInput.CurrentSession();
        if (console is not null)
        {
            Assert.NotNull(input);
            Assert.NotEmpty(input.Identity);
        }
    }

    /// <summary>電源設定を確認した要求を構成します。</summary>
    /// <returns>固定した対象と版です。</returns>
    private static DeviceChangeRequest Request(DeviceSnapshot before, DevicePowerChange change) => new(Guid.NewGuid(), "host", Guid.NewGuid(), EDeviceAction.Power,
        new(EDeviceArea.Power), before.Revision, DeviceSnapshots.Create(change).Data, true);

    /// <summary>値と部分実行を記録するOS境界です。</summary>
    private sealed class Access : IWindowsPowerAccess
    {
        internal DevicePowerState State
        {
            get; set;
        } = new(Guid.NewGuid(), "Balanced", new(600, 900, 3600, 3, 120), new(600, 900, 3600, 3, 120), true, true, true, new(), []);

        internal List<string> Calls
        {
            get;
        } = [];

        internal bool FailActivate
        {
            get; init;
        }

        /// <summary>試験中の状態を取得します。</summary>
        /// <returns>最後の値です。</returns>
        public DevicePowerState Read() => State;

        /// <summary>この試験が変更する表示時間を記録します。</summary>
        public void Write(Guid scheme, bool ac, string setting, uint value)
        {
            Assert.Equal(State.SchemeId, scheme);
            Assert.Equal(nameof(DevicePowerValues.DisplaySeconds), setting);
            Assert.True(ac);
            Calls.Add("ac." + setting);
            State = State with { Ac = State.Ac with { DisplaySeconds = value } };
        }

        /// <summary>プランの反映を確認、または失敗させます。</summary>
        public void Activate(Guid scheme)
        {
            Assert.Equal(State.SchemeId, scheme);
            if (FailActivate)
            {
                throw new Win32Exception(5);
            }
            Calls.Add("activate");
        }

        /// <summary>高速スタートアップだけを更新します。</summary>
        public void SetFastStartup(bool enabled)
        {
            Calls.Add("startup");
            State = State with { FastStartup = enabled };
        }

        /// <summary>PC共通の停止設定を保存します。</summary>
        public void SetIdleShutdown(DeviceIdleShutdownPolicy policy)
        {
            Calls.Add("idle");
            State = State with { IdleShutdown = policy };
        }
    }
}
