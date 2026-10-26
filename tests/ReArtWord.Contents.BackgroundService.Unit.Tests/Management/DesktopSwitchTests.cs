using System.Diagnostics;
using System.Runtime.InteropServices;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop.Input;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop.Video;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>表示desktopを切り替えず、取得先とdesktop変更時の寿命を検証します。</summary>
public sealed class DesktopSwitchTests
{
    /// <summary>desktop変更は取得だけを取消し、接続側tokenへ波及しません。</summary>
    /// <returns>desktop監視の終了です。</returns>
    [Fact]
    public async Task DesktopChangeCancelsOnlyTheCaptureAsync()
    {
        using CancellationTokenSource connection = new(TimeSpan.FromSeconds(3));
        using CancellationTokenSource capture = CancellationTokenSource.CreateLinkedTokenSource(connection.Token);
        Assert.True(await DesktopVideo.WatchDesktopAsync(() => false, capture, capture.Token).ConfigureAwait(true));
        Assert.True(capture.IsCancellationRequested);
        Assert.False(connection.IsCancellationRequested);
    }

    /// <summary>外部取消をdesktop変更と誤認して取得を再開しません。</summary>
    /// <returns>取消された監視の終了です。</returns>
    // 抑制理由: 監視を開始してから取り消し、finallyで監視の終了を待ってから取消資源を破棄します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2025", Justification = "finallyで監視TaskをawaitしてからCancellationTokenSourceを破棄します。")]
    [Fact]
    public async Task ConnectionCancellationIsNotADesktopTransitionAsync()
    {
        using CancellationTokenSource capture = new();
        TaskCompletionSource observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> watching = DesktopVideo.WatchDesktopAsync(() =>
        {
            observed.TrySetResult();
            return true;
        }, capture, capture.Token);
        try
        {
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            await capture.CancelAsync().ConfigureAwait(true);
            Assert.False(await watching.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true));
        }
        finally
        {
            await capture.CancelAsync().ConfigureAwait(true);
            await watching.ConfigureAwait(true);
        }
    }

    /// <summary>起動desktopを明示した子processが、そのdesktop名を実際に報告します。</summary>
    /// <returns>非表示の試験desktopと子processの回収です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureProcessUsesTheSpecifiedDesktopWithoutChangingTheVisibleDesktopAsync(bool executableNameOnly)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        string name = "Contents-Test-" + Guid.NewGuid().ToString("N");
        string path = Path.Combine(Path.GetTempPath(), name + ".txt");
        string? before = WindowsInputDesktop.Name();
        IntPtr desktop = CreateDesktopW(name, IntPtr.Zero, IntPtr.Zero, 0, 0x1ff, IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, desktop);
        try
        {
            string shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            if (executableNameOnly)
            {
                shell = Path.GetFileNameWithoutExtension(shell);
            }
            ProcessStartInfo info = new(shell);
            info.ArgumentList.Add("-NoProfile");
            info.ArgumentList.Add("-NonInteractive");
            info.ArgumentList.Add("-Command");
            info.ArgumentList.Add("Add-Type 'using System;using System.Runtime.InteropServices;public static class D{[DllImport(\"user32.dll\")]public static extern IntPtr GetThreadDesktop(uint id);[DllImport(\"kernel32.dll\")]public static extern uint GetCurrentThreadId();[DllImport(\"user32.dll\",CharSet=CharSet.Unicode)]public static extern bool GetUserObjectInformationW(IntPtr h,int i,char[] b,int n,out int r);}';" +
                "$b=New-Object char[] 256;$r=0;[void][D]::GetUserObjectInformationW([D]::GetThreadDesktop([D]::GetCurrentThreadId()),2,$b,512,[ref]$r);[IO.File]::WriteAllText('" + path.Replace("'", "''", StringComparison.Ordinal) + "',([string]::new($b)).Trim([char]0))");
            using Process process = DesktopWorkerProcess.StartCapture(info, name);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(true);
                Assert.Equal(0, process.ExitCode);
                Assert.Equal(name, await File.ReadAllTextAsync(path, timeout.Token).ConfigureAwait(true));
                Assert.Equal(before, WindowsInputDesktop.Name());
                Assert.True(WindowsInputDesktop.TryAttach(out WindowsInputDesktop input));
                input.Dispose();
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(true);
            }
        }
        finally
        {
            Assert.True(CloseDesktop(desktop));
            File.Delete(path);
        }
    }

    /// <summary>表示しない試験desktopを作成します。</summary>
    /// <returns>試験が所有するhandleです。</returns>
    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr CreateDesktopW(string name, IntPtr device, IntPtr mode, uint flags, uint access, IntPtr security);

    /// <summary>試験desktopのhandleを解放します。</summary>
    /// <returns>解放成功です。</returns>
    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);
}
