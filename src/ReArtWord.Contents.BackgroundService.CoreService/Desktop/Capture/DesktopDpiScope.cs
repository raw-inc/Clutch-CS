using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ReArtWord.Contents.BackgroundService.CoreService.Desktop.Capture;

/// <summary>物理画面の座標を扱う同期区間だけDPI仮想化を無効にし、借用threadを元へ戻します。</summary>
[SupportedOSPlatform("windows")]
internal readonly partial struct DesktopDpiScope : IDisposable
{
    private readonly IntPtr previous;

    /// <summary>await後の別threadでも、その呼出区間の座標系を明示します。</summary>
    public DesktopDpiScope()
    {
        previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        if (previous == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    /// <summary>同じthreadの呼出元のDPI設定を復元します。</summary>
    public void Dispose()
    {
        if (previous != IntPtr.Zero)
        {
            _ = SetThreadDpiAwarenessContext(previous);
        }
    }

    /// <summary>threadに所属するDPI座標系を切り替えます。</summary>
    /// <returns>復元用の以前の設定です。</returns>
    [LibraryImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
