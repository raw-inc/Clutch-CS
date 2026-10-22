using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;

namespace ReArtWord.Contents.BackgroundService.CoreService.Windows;

/// <summary>Webクライアントの起動ユーザーとセッションを保ってUpdaterを起動します。</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsClientUpdateLaunch
{
    /// <summary>停止対象の実体と権限を確認してから、独立helperを起動します。</summary>
    /// <returns>呼出元が監視するhelperです。</returns>
    internal static Process Start(WebClientUpdateJob job, string helper, string jobPath)
    {
        WebClientUpdateProbe.VerifyProcess(job.Installation, job.PreviousProcess);
        using Process previous = Process.GetProcessById(job.PreviousProcess.ProcessId);
        if (!WindowsProcessNative.OpenProcessToken(previous.SafeHandle, 0x000A, out SafeAccessTokenHandle source))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        using (source)
        using (WindowsIdentity owner = new(source.DangerousGetHandle()))
        using (WindowsIdentity caller = WindowsIdentity.GetCurrent())
        {
            if (job.Installation.RunAsService != owner.IsSystem)
            {
                throw new InvalidOperationException("The web client process does not use its registered startup identity.");
            }
            if (owner.IsSystem && caller.IsSystem)
            {
                return SetupWorkspace.Launch(helper, jobPath, false, "--update-web-client");
            }
            if (!caller.IsSystem)
            {
                using Process current = Process.GetCurrentProcess();
                if (caller.User != owner.User || current.SessionId != previous.SessionId || new WindowsPrincipal(caller).IsInRole(WindowsBuiltInRole.Administrator))
                {
                    throw new InvalidOperationException("The Core service cannot launch the updater as the web client's user.");
                }
                return SetupWorkspace.Launch(helper, jobPath, false, "--update-web-client");
            }
            if (!WindowsProcessNative.DuplicateTokenEx(source, 0x02000000, IntPtr.Zero, 2, 1, out SafeAccessTokenHandle primary))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            using (primary)
            {
                return WindowsProcessLaunch.Start(primary, helper, ["--update-web-client", jobPath], Path.GetDirectoryName(helper)!);
            }
        }
    }
}
