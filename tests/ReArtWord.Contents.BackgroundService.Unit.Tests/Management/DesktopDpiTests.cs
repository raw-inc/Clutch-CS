using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop.Capture;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>列挙と入力を行う各threadの座標系を独立に設定・復元します。</summary>
[SupportedOSPlatform("windows")]
public sealed class DesktopDpiTests
{
    /// <summary>物理座標の区間をネストしても、元のDPI状態を汚染しません。</summary>
    [Fact]
    public void PhysicalScopeRestoresTheCallingThreadAndMonitorEnumeration()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        IntPtr original = SetThreadDpiAwarenessContext(new IntPtr(-1));
        try
        {
            Assert.Equal(0, GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext()));
            Assert.NotEmpty(WindowsDesktop.Monitors());
            Assert.Equal(0, GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext()));
            using (new DesktopDpiScope())
            {
                Assert.Equal(2, GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext()));
                using (new DesktopDpiScope())
                {
                    Assert.Equal(2, GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext()));
                }
                Assert.Equal(2, GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext()));
            }
            Assert.Equal(0, GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext()));
        }
        finally
        {
            _ = SetThreadDpiAwarenessContext(original);
        }
    }

    /// <summary>このthreadのDPI状態を取得します。</summary>
    /// <returns>借用DPI状態です。</returns>
    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetThreadDpiAwarenessContext();

    /// <summary>状態をDPI対応レベルへ変換します。</summary>
    /// <returns>未対応0、システム1、画面別2です。</returns>
    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);

    /// <summary>試験内のDPI設定を適用・復元します。</summary>
    /// <returns>以前の設定です。</returns>
    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
