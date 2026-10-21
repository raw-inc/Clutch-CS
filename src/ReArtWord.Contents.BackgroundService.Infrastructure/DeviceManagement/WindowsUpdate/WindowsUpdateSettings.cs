using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.WindowsUpdate;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.WindowsUpdate;

/// <summary>プレビューした更新ポリシーを再検証し、変更した段階を保存します。</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsUpdateSettings(IWindowsUpdateAccess access)
{
    /// <summary>Windowsのポリシー管理へ接続します。</summary>
    internal WindowsUpdateSettings() : this(new WindowsUpdateAccess())
    {
    }

    /// <summary>設定を変更せず現在値を取得します。</summary>
    /// <returns>内容版を持つポリシーです。</returns>
    internal DeviceSnapshot Read() => DeviceSnapshots.Create(access.Read());

    /// <summary>確認済み対象を一度だけ変更します。</summary>
    /// <returns>完了済み段階と結果です。</returns>
    internal async Task<DeviceChangeResult> ApplyAsync(DeviceChangeRequest request, DeviceSnapshot before, CancellationToken cancellationToken = default)
    {
        DeviceWindowsUpdateChange change = DeviceSnapshots.Read<DeviceWindowsUpdateChange>(request.Change);
        DeviceSnapshot current = Read();
        if (!string.Equals(current.Revision, before.Revision, StringComparison.Ordinal))
        {
            return new(request.OperationId, EOutcome.NotExecuted, "Device.SettingsChanged");
        }
        try
        {
            DeviceWindowsUpdateChange.Validate(DeviceSnapshots.Read<DeviceWindowsUpdateState>(current.Data));
        }
        catch (ArgumentException exception)
        {
            return new(request.OperationId, EOutcome.NotExecuted, exception.Message);
        }
        List<string> steps = [];
        try
        {
            await access.WriteAsync(change, steps.Add, cancellationToken).ConfigureAwait(false);
            return new(request.OperationId, EOutcome.Succeeded, CompletedSteps: steps);
        }
        catch (Exception exception) when (exception is COMException or Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return new(request.OperationId, EOutcome.Failed, "Device.UpdateChangeFailed", CompletedSteps: steps);
        }
    }
}
