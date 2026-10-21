using System.ComponentModel;
using System.Runtime.Versioning;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Power;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Power;

/// <summary>電源変更を部分適用し、完了した段階を失敗時にも残します。</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsPowerSettings(IWindowsPowerAccess access)
{
    /// <summary>Windowsの正式な電源設定APIへ接続します。</summary>
    internal WindowsPowerSettings() : this(new WindowsPowerAccess())
    {
    }

    /// <summary>設定だけを内容版へ含め、刻々変わる無操作時間は含めません。</summary>
    /// <returns>再確認できる現在値です。</returns>
    internal DeviceSnapshot Read() => DeviceSnapshots.Create(access.Read());

    /// <summary>既読のプランと内容版を再検証してから、指定項目だけを書き込みます。</summary>
    /// <returns>開始前拒否または実行済み段階を伴う結果です。</returns>
    internal DeviceChangeResult Apply(DeviceChangeRequest request, DeviceSnapshot before)
    {
        DevicePowerChange change = DeviceSnapshots.Read<DevicePowerChange>(request.Change);
        DeviceSnapshot latest = Read();
        if (!string.Equals(latest.Revision, before.Revision, StringComparison.Ordinal))
        {
            return new(request.OperationId, EOutcome.NotExecuted, "Device.SettingsChanged");
        }
        DevicePowerState state = DeviceSnapshots.Read<DevicePowerState>(latest.Data);
        try
        {
            change.Validate(state);
        }
        catch (ArgumentException exception)
        {
            return new(request.OperationId, EOutcome.NotExecuted, exception.Message);
        }
        List<string> steps = [];
        try
        {
            WriteValues(state.SchemeId, true, change.Ac, steps);
            WriteValues(state.SchemeId, false, change.Dc, steps);
            if (steps.Count != 0)
            {
                access.Activate(state.SchemeId);
                steps.Add("power.planApplied");
            }
            if (change.FastStartup is bool fast)
            {
                access.SetFastStartup(fast);
                steps.Add("power.fastStartupSaved");
            }
            if (change.IdleShutdownAcSeconds is not null || change.IdleShutdownDcSeconds is not null)
            {
                access.SetIdleShutdown(new(change.IdleShutdownAcSeconds ?? state.IdleShutdown.AcSeconds,
                    change.IdleShutdownDcSeconds ?? state.IdleShutdown.DcSeconds, Guid.NewGuid(), DateTimeOffset.UtcNow));
                steps.Add("power.idleShutdownSaved");
            }
            return new(request.OperationId, EOutcome.Succeeded, CompletedSteps: steps);
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new(request.OperationId, EOutcome.Failed, "Device.PowerChangeFailed", CompletedSteps: steps);
        }
    }

    /// <summary>明示された項目だけをOSへ保存します。</summary>
    private void WriteValues(Guid scheme, bool ac, DevicePowerValues? values, List<string> steps)
    {
        foreach ((string name, uint? value) in values?.Items() ?? new Dictionary<string, uint?>())
        {
            if (value is not uint selected)
            {
                continue;
            }
            access.Write(scheme, ac, name, selected);
            string supply = ac ? "ac"
                               : "dc";
            steps.Add($"power.{supply}.{name}");
        }
    }
}
