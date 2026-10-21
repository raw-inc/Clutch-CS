using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Tests.Shared;

/// <summary>画面・音声を使用できないホステッドCIで、実デスクトップ試験を明示的に除外します。</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed partial class HostedDesktopFactAttribute : FactAttribute
{
    /// <summary>対話セッション・画面・音声出力と、必要に応じて通知領域を確認します。</summary>
    public HostedDesktopFactAttribute(bool requireShell = false)
    {
        RequireShell = requireShell;
        Skip = GetSkipReason(requireShell);
    }

    /// <summary>通知領域を必要とする試験かどうかです。</summary>
    public bool RequireShell
    {
        get;
    }

    /// <summary>機能不足による除外をGitHubホステッドランナーだけに限定します。</summary>
    internal static bool IsHostedRunner => string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase)
        && string.Equals(Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT"), "github-hosted", StringComparison.OrdinalIgnoreCase);

    /// <summary>ローカル・実機ランナーは除外せず、ホステッドCIの不足機能を調べます。</summary>
    /// <returns>実行不能の理由、または通常実行を表すnullです。</returns>
    internal static string? GetSkipReason(bool requireShell = false)
    {
        if (OperatingSystem.IsWindows() && IsHostedRunner)
        {
            using Process process = Process.GetCurrentProcess();
            int monitors = GetSystemMetrics(80);
            uint audioDevices = WaveOutGetNumDevs();
            bool shellAvailable = FindWindowW("Shell_TrayWnd", null) != IntPtr.Zero;

            // workerは映像選択・注釈購読より前に音声取得を開始します。
            if (process.SessionId == 0 || monitors == 0 || audioDevices == 0 || requireShell && !shellAvailable)
            {
                return $"デスクトップ試験に必要な機能がありません。session={process.SessionId}, monitors={monitors}, audioDevices={audioDevices}, shellRequired={requireShell}, shellAvailable={shellAvailable}。必要な機能を使用できるWindowsで実行してください。";
            }
        }
        return null;
    }

    /// <summary>試験セッションから使用できる画面数を確認します。</summary>
    /// <returns>指定したWindows表示情報です。</returns>
    [LibraryImport("user32.dll", EntryPoint = "GetSystemMetrics")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetSystemMetrics(int index);

    /// <summary>無音再生に必要な出力デバイスの有無を確認します。</summary>
    /// <returns>Windowsが公開する出力デバイス数です。</returns>
    [LibraryImport("winmm.dll", EntryPoint = "waveOutGetNumDevs")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint WaveOutGetNumDevs();

    /// <summary>通知領域を提供するWindows Shellを確認します。</summary>
    /// <returns>対象window、存在しなければゼロです。</returns>
    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr FindWindowW(string className, string? windowName);
}
