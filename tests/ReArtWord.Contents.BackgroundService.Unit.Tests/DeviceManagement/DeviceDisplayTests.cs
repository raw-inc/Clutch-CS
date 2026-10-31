using System.Runtime.Versioning;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Display;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Display;
using ReArtWord.Contents.BackgroundService.Tests.Shared;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.DeviceManagement;

/// <summary>実画面を変更せず、表示接続・候補・OSの成否を検証します。</summary>
[SupportedOSPlatform("windows")]
public sealed class DeviceDisplayTests
{
    /// <summary>一覧と個別照会で内容版を共有し、候補のモードを固定したデバイスへ渡します。</summary>
    /// <returns>OS境界へ渡す対象と反映後の照合です。</returns>
    [Fact]
    public async Task AppliesOnlyAnAdvertisedModeToTheVerifiedDisplayAsync()
    {
        Driver driver = new();
        WindowsDisplaySettings platform = new(driver);
        DeviceSnapshot list = await platform.ReadAsync(new(EDeviceArea.Display)).ConfigureAwait(true);
        DeviceDisplayInfo display = Assert.Single(DeviceSnapshots.Read<DeviceDisplayState>(list.Data).Displays);
        DeviceSnapshot before = await platform.ReadAsync(new(EDeviceArea.Display, display.Id)).ConfigureAwait(true);
        Assert.Equal(display.Revision, before.Revision);
        DeviceChangeRequest request = Request(driver, before);
        Assert.Equal(EOutcome.Succeeded, (await platform.ApplyAsync(request, before).ConfigureAwait(true)).Outcome);
        Assert.Equal(1, driver.Calls);
        Assert.Equal(driver.State.DeviceName, driver.Device);
        Assert.True(DeviceChangeConfirmation.Matches(request, await platform.ReadAsync(request.Read).ConfigureAwait(true)));
    }

    /// <summary>別の表示状態・未対応候補・ドライバーの試験拒否では書込みません。</summary>
    /// <returns>副作用開始前の拒否です。</returns>
    [Theory]
    [InlineData("changed")]
    [InlineData("unsupported")]
    [InlineData("testRejected")]
    public async Task RejectsUnverifiedChangesBeforeWritingAsync(string problem)
    {
        Driver driver = new();
        WindowsDisplaySettings platform = new(driver);
        DeviceSnapshot before = await platform.ReadAsync(new(EDeviceArea.Display, driver.State.Id)).ConfigureAwait(true);
        DeviceChangeRequest request = Request(driver, before);
        switch (problem)
        {
            case "changed":
                driver.State = driver.State with { SessionId = 3 };
                break;
            case "unsupported":
                request = request with { Change = DeviceSnapshots.Create(new DeviceDisplayChange(driver.Mode with { RefreshRate = 144 })).Data };
                break;
            default:
                driver.TestResult = -2;
                break;
        }
        Assert.Equal(EOutcome.NotExecuted, (await platform.ApplyAsync(request, before).ConfigureAwait(true)).Outcome);
        Assert.Equal(0, driver.Calls);
    }

    /// <summary>再起動が必要な結果や失敗を、表示反映の成功としません。</summary>
    /// <returns>OSの結果と再読取り不一致の区別です。</returns>
    [Theory]
    [InlineData(1, EOutcome.Unknown)]
    [InlineData(-1, EOutcome.Failed)]
    public async Task PreservesDriverFailureAndRestartRequirementAsync(int code, EOutcome outcome)
    {
        Driver driver = new() { ApplyResult = code };
        WindowsDisplaySettings platform = new(driver);
        DeviceSnapshot before = await platform.ReadAsync(new(EDeviceArea.Display, driver.State.Id)).ConfigureAwait(true);
        DeviceChangeRequest request = Request(driver, before);
        Assert.Equal(outcome, (await platform.ApplyAsync(request, before).ConfigureAwait(true)).Outcome);
        Assert.False(DeviceChangeConfirmation.Matches(request, before));
    }

    /// <summary>実Windowsの列挙だけを行い、表示変更関数は呼びません。</summary>
    /// <returns>ネイティブ構造体の寸法と個別版の確認です。</returns>
    [WindowsFact]
    public async Task ReadsRealConsoleWithoutChangingItsModeAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        WindowsDisplaySettings platform = new();
        DeviceSnapshot state = await platform.ReadAsync(new(EDeviceArea.Display)).ConfigureAwait(true);
        foreach (DeviceDisplayInfo display in DeviceSnapshots.Read<DeviceDisplayState>(state.Data).Displays)
        {
            Assert.True(0 < display.Current.Width && 0 < display.Current.Height);
            Assert.NotEmpty(display.Modes);
            Assert.Equal(display.Revision, (await platform.ReadAsync(new(EDeviceArea.Display, display.Id)).ConfigureAwait(true)).Revision);
        }
    }

    /// <summary>確認済みの一つのディスプレイを指定します。</summary>
    /// <returns>再実行しない変更要求です。</returns>
    private static DeviceChangeRequest Request(Driver driver, DeviceSnapshot before) => new(Guid.NewGuid(), "host", Guid.NewGuid(), EDeviceAction.Display,
        new(EDeviceArea.Display, driver.State.Id), before.Revision, DeviceSnapshots.Create(new DeviceDisplayChange(driver.Mode)).Data, true);

    /// <summary>操作と状態を記録する表示ドライバーの試験境界です。</summary>
    private sealed class Driver : IWindowsDisplayDriver
    {
        internal DeviceDisplayMode Mode
        {
            get;
        } = new(1920, 1080, 60, 32, 0);

        internal DeviceDisplayInfo State
        {
            get; set;
        } = new(new string('a', 64), @"\\.\DISPLAY1", "Monitor", true, 1,

            new(1280, 720, 60, 32, 0), [new(1280, 720, 60, 32, 0), new(1920, 1080, 60, 32, 0)]);
        internal int Calls
        {
            get; private set;
        }

        internal int TestResult
        {
            get; set;
        }

        internal int ApplyResult
        {
            get; set;
        }

        internal string? Device
        {
            get; private set;
        }

        /// <summary>現在の表示を取得します。</summary>
        /// <returns>同じ接続の表示設定です。</returns>
        public IReadOnlyList<DeviceDisplayInfo> Read() => [State];

        /// <summary>設定を変更せずドライバーの試験結果を返します。</summary>
        /// <returns>指定済みの結果です。</returns>
        public int Test(string device, DeviceDisplayMode mode) => TestResult;

        /// <summary>対象を記録し、成功時だけ現在値へ反映します。</summary>
        /// <returns>指定済みの結果です。</returns>
        public int Apply(string device, DeviceDisplayMode mode)
        {
            Calls++;
            Device = device;
            if (ApplyResult == 0)
            {
                State = State with { Current = mode };
            }
            return ApplyResult;
        }
    }
}
