using System.Diagnostics;
using ReArtWord.Contents.BackgroundService.Tests.Shared;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>FFmpegで画面を取得できないホステッドCIだけ、実画面取得を除外します。</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class HostedDesktopDuplicationFactAttribute : FactAttribute
{
    /// <summary>製品の引数を使わず、最小のddagrabで画面取得機能を確認します。</summary>
    public HostedDesktopDuplicationFactAttribute()
    {
        Skip = WindowsFactAttribute.GetSkipReason();
        if (Skip is not null)
        {
            return;
        }
        string? executable = Environment.GetEnvironmentVariable("CONTENTS_TEST_FFPROBE");
        if (!OperatingSystem.IsWindows() || !HostedDesktopFactAttribute.IsHostedRunner || !File.Exists(executable))
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
}
