using System.Runtime.Versioning;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Display;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Display;

/// <summary>表示接続・開始前の内容版・対応モードを固定して変更します。</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDisplaySettings : IDeviceDisplayAccess
{
    private readonly IWindowsDisplayDriver driver;

    /// <summary>対話コンソールで動くWindows APIを使用します。</summary>
    public WindowsDisplaySettings() : this(new WindowsDisplayDriver())
    {
    }

    /// <summary>OS操作を分離した境界試験で使用します。</summary>
    internal WindowsDisplaySettings(IWindowsDisplayDriver driver)
    {
        this.driver = driver;
    }

    /// <summary>表示名ではなく接続識別子に対応する現在値を返します。</summary>
    /// <returns>表示一覧または選択した表示接続の内容版です。</returns>
    public Task<DeviceSnapshot> ReadAsync(DeviceReadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<DeviceDisplayInfo> displays = driver.Read();
        if (request.ResourceId is null)
        {
            return Task.FromResult(DeviceSnapshots.Create(new DeviceDisplayState(displays.Select(item => item with { Revision = ContractJson.Hash(item with { Revision = null }) }).ToArray())));
        }
        DeviceDisplayInfo display = displays.SingleOrDefault(item => string.Equals(item.Id, request.ResourceId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Device.DisplayChanged");
        return Task.FromResult(DeviceSnapshots.Create(display with { Revision = null }));
    }

    /// <summary>別画面への付替えや非対応モードを拒否し、モードだけを永続変更します。</summary>
    /// <returns>表示ドライバーが確認した変更結果です。</returns>
    public async Task<DeviceChangeResult> ApplyAsync(DeviceChangeRequest request, DeviceSnapshot before, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(before);
        DeviceChangeRules.Validate(request);
        DeviceSnapshot current = await ReadAsync(request.Read, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(current.Revision, before.Revision, StringComparison.Ordinal))
        {
            return new(request.OperationId, EOutcome.NotExecuted, "Device.SettingsChanged");
        }
        DeviceDisplayInfo display = DeviceSnapshots.Read<DeviceDisplayInfo>(current.Data);
        DeviceDisplayMode mode = DeviceSnapshots.Read<DeviceDisplayChange>(request.Change).Mode;
        if (!display.Modes.Contains(mode) || driver.Test(display.DeviceName, mode) != 0)
        {
            return new(request.OperationId, EOutcome.NotExecuted, "Device.UnsupportedDisplayMode");
        }
        cancellationToken.ThrowIfCancellationRequested();
        int result = driver.Apply(display.DeviceName, mode);
        return result switch
        {
            0 => new(request.OperationId, EOutcome.Succeeded, CompletedSteps: ["display.modeApplied"]),
            1 => new(request.OperationId, EOutcome.Unknown, "Device.DisplayRestartRequired", CompletedSteps: ["display.registryUpdated"]),
            _ => new(request.OperationId, EOutcome.Failed, "Device.DisplayChangeFailed"),
        };
    }
}
