using System.Diagnostics;
using System.Runtime.Versioning;
using ReArtWord.Contents.BackgroundService.Contracts.Desktop;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop.Capture;
using ReArtWord.Contents.BackgroundService.Tests.Shared;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>DDAの対象画面を確認し、実画面取得を検証できない場合は理由を示します。</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class DesktopDuplicationFactAttribute : FactAttribute
{
    /// <summary>対象画面を選び、ホステッドCIでは最小のddagrabによる取得も確認します。</summary>
    public DesktopDuplicationFactAttribute()
    {
        Skip = WindowsFactAttribute.GetSkipReason();
        if (Skip is not null || !OperatingSystem.IsWindows())
        {
            return;
        }
        if (FindMonitor() is null)
        {
            Skip = "Desktop Duplicationで取得する画面を特定できないため、フレームの受け渡しを検証できません。";
            return;
        }
        string? executable = Environment.GetEnvironmentVariable("CONTENTS_TEST_FFPROBE");
        if (!HostedDesktopFactAttribute.IsHostedRunner || !File.Exists(executable))
        {
            return;
        }
        ProcessStartInfo start = new(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "-v", "error", "-f", "lavfi", "-i", "ddagrab=framerate=1:draw_mouse=0", "-show_entries", "stream=width,height", "-of", "json" })
        {
            start.ArgumentList.Add(argument);
        }
        using Process process = new() { StartInfo = start };
        if (!process.Start())
        {
            throw new IOException("FFprobe did not start.");
        }
        if (!process.WaitForExit(10000))
        {
            process.Kill(true);
            if (!process.WaitForExit(5000))
            {
                throw new TimeoutException("FFprobe did not stop after the capability probe timed out.");
            }
            throw new TimeoutException("FFprobe desktop capability probe timed out.");
        }
        string error = process.StandardError.ReadToEnd().Trim();
        if (process.ExitCode != 0)
        {
            Skip = $"FFmpegのDesktop Duplicationを使用できません。exitCode={process.ExitCode}。対応するGPUとディスプレイドライバーがあるWindowsで実行してください。{error}";
        }
    }

    /// <summary>製品がDDAに対応付けられる画面だけを、この試験の取得対象にします。</summary>
    /// <returns>対象画面。GDIへのフォールバックが必要な場合はnullです。</returns>
    [SupportedOSPlatform("windows")]
    internal static DesktopMonitor? FindMonitor()
    {
        foreach (DesktopMonitor candidate in WindowsDesktop.Monitors())
        {
            if (DesktopDuplication.FindOutput(candidate.Name) is not null)
            {
                return candidate;
            }
        }
        return null;
    }
}
