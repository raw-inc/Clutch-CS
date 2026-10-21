using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop.Clipboard;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Desktop;

namespace ReArtWord.Contents.BackgroundService.DesktopFixture;

/// <summary>利用者のクリップボードを触らず、別window stationで製品のWin32境界を検証します。</summary>
[SupportedOSPlatform("windows")]
internal static class ClipboardScenario
{
    /// <summary>子プロセスだけを専用領域へ移し、空文字・Unicode・上限値の所有権移譲を確認します。</summary>
    /// <returns>実APIを確認できた場合だけ0です。</returns>
    internal static int Run()
    {
        IntPtr previousStation = GetProcessWindowStation(), previousDesktop = GetThreadDesktop(GetCurrentThreadId());

        // 管理者の試験でだけ固有名を作成し、CREATE_ONLYで既存領域の共有を拒否します。
        IntPtr station = CreateWindowStationW("ContentsClipboardTest" + Guid.NewGuid().ToString("N"), 1, 0x000F037F, IntPtr.Zero);
        if (previousStation == IntPtr.Zero || previousDesktop == IntPtr.Zero || station == IntPtr.Zero || station == previousStation)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        IntPtr desktop = IntPtr.Zero;
        try
        {
            if (!SetProcessWindowStation(station))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            desktop = CreateDesktopW("clipboard", null, IntPtr.Zero, 0, 0x01ff, IntPtr.Zero);
            if (desktop == IntPtr.Zero || !SetThreadDesktop(desktop) || GetProcessWindowStation() != station)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            if (WindowsDesktopClipboard.ReadText() is not null)
            {
                throw new InvalidOperationException("The private clipboard must start empty.");
            }
            foreach (string value in new[] { "", "日本語\r\n🌐", new string('x', DesktopClipboardWire.MaximumTextBytes), "after-large-text" })
            {
                uint before = WindowsDesktopClipboard.Sequence();
                if (!WindowsDesktopClipboard.TryWriteText(value) || WindowsDesktopClipboard.Sequence() == before
                    || !string.Equals(value, WindowsDesktopClipboard.ReadText(), StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Private clipboard round trip failed.");
                }
            }
            return 0;
        }
        finally
        {
            _ = SetProcessWindowStation(previousStation);
            _ = SetThreadDesktop(previousDesktop);
            if (desktop != IntPtr.Zero)
            {
                _ = CloseDesktop(desktop);
            }
            _ = CloseWindowStation(station);
        }
    }

    /// <summary>子プロセスの現在領域を借用します。</summary>
    /// <returns>借用handleです。</returns>
    [DllImport("user32.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetProcessWindowStation();

    /// <summary>独立したクリップボードを所有する試験領域を作ります。</summary>
    /// <returns>試験だけが所有するhandleです。</returns>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr CreateWindowStationW(string? name, uint flags, uint access, IntPtr attributes);

    /// <summary>子プロセスの接続領域だけを変更します。</summary>
    /// <returns>変更結果です。</returns>
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWindowStation(IntPtr station);

    /// <summary>作成した試験領域を回収します。</summary>
    /// <returns>解放結果です。</returns>
    [DllImport("user32.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseWindowStation(IntPtr station);

    /// <summary>現在threadの復帰先を借用します。</summary>
    /// <returns>借用desktopです。</returns>
    [DllImport("user32.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetThreadDesktop(uint thread);

    /// <summary>同期試験のthreadを識別します。</summary>
    /// <returns>現在threadのIDです。</returns>
    [DllImport("kernel32.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetCurrentThreadId();

    /// <summary>試験windowだけが属するdesktopを作ります。</summary>
    /// <returns>試験所有のdesktopです。</returns>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr CreateDesktopW(string name, string? device, IntPtr mode, uint flags, uint access, IntPtr attributes);

    /// <summary>同期処理中のthreadを試験desktopへ接続します。</summary>
    /// <returns>接続結果です。</returns>
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadDesktop(IntPtr desktop);

    /// <summary>試験所有のdesktopだけを閉じます。</summary>
    /// <returns>解放結果です。</returns>
    [DllImport("user32.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);
}
