using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ReArtWord.Contents.BackgroundService.CoreService.Desktop.Input;

/// <summary>入力desktopの確認と同期呼出中だけのthread接続を所有します。</summary>
[SupportedOSPlatform("windows")]
internal readonly partial struct WindowsInputDesktop : IDisposable
{
    private readonly IntPtr desktop;
    private readonly IntPtr previous;

    /// <summary>借用元threadのdesktopと一時handleを保持します。</summary>
    private WindowsInputDesktop(IntPtr desktop, IntPtr previous)
    {
        this.desktop = desktop;
        this.previous = previous;
    }

    /// <summary>awaitを挟まず同じthreadで入力し、必ず元desktopへ戻します。</summary>
    /// <returns>OSが入力desktopへのアクセスを許可したかです。</returns>
    internal static bool TryAttach(out WindowsInputDesktop scope)
    {
        scope = default;

        // SendInputにもDESKTOP_JOURNALPLAYBACKが必要です。接続だけ成功する
        // 権限でthreadを付け替えると、最初の入力がERROR_ACCESS_DENIEDになります。
        uint inputAccess = 0x0001 | 0x0002 | 0x0020 | 0x0080 | 0x0100;
        IntPtr desktop = OpenInputDesktop(0, false, inputAccess);
        if (desktop == IntPtr.Zero)
        {
            return false;
        }
        IntPtr previous = GetThreadDesktop(GetCurrentThreadId());
        if (!SetThreadDesktop(desktop))
        {
            _ = CloseDesktop(desktop);
            return false;
        }
        scope = new(desktop, previous);
        return true;
    }

    /// <summary>起動先に使う入力desktop名です。アクセス不可・切替中はnullです。</summary>
    /// <returns>OSから取得したdesktop名です。</returns>
    internal static string? Name()
    {
        char[] buffer = new char[256];
        int length = ReadName(buffer);
        return length == 0 ? null
                           : new string(buffer, 0, length);
    }

    /// <summary>監視用の領域を再利用し、定常pollで文字列を生成しません。</summary>
    /// <returns>起動時と同じ入力desktopかです。</returns>
    internal static bool IsCurrent(string expected, char[] buffer)
    {
        int length = ReadName(buffer);
        return buffer.AsSpan(0, length).Equals(expected.AsSpan(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>入力desktop名を既存領域へ読み、handleを同じ呼出内で閉じます。</summary>
    /// <returns>終端を除く文字数です。</returns>
    private static int ReadName(char[] buffer)
    {
        IntPtr desktop = OpenInputDesktop(0, false, 1);
        if (desktop == IntPtr.Zero)
        {
            return 0;
        }
        try
        {
            if (!GetUserObjectInformationUnsafe(desktop, 2, buffer, buffer.Length * sizeof(char), out int required))
            {
                return 0;
            }
            return Math.Max(0, required / sizeof(char) - 1);
        }
        finally
        {
            _ = CloseDesktop(desktop);
        }
    }

    /// <summary>OS入力終了後、threadを元へ戻して一時handleを閉じます。</summary>
    public void Dispose()
    {
        if (desktop == IntPtr.Zero)
        {
            return;
        }
        bool restored = SetThreadDesktop(previous);
        int error = Marshal.GetLastWin32Error();
        _ = CloseDesktop(desktop);
        if (!restored)
        {
            throw new Win32Exception(error);
        }
    }

    /// <summary>現在表示中の入力desktopを開きます。</summary>
    /// <returns>呼出元が閉じるhandleです。</returns>
    [LibraryImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);

    /// <summary>desktop名を取得します。</summary>
    /// <returns>取得成功です。</returns>
    private static unsafe bool GetUserObjectInformationUnsafe(IntPtr handle, int index, char[] information, int size, out int required)
    {
        fixed (char* buffer = information)
        {
            return ReadUserObjectInformationUnsafe(handle, index, buffer, size, out required);
        }
    }

    /// <summary>固定したUTF-16領域へ、byte単位の上限でdesktop名を読みます。</summary>
    /// <returns>取得成功です。</returns>
    [LibraryImport("user32.dll", EntryPoint = "GetUserObjectInformationW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool ReadUserObjectInformationUnsafe(IntPtr handle, int index, char* information, int size, out int required);

    /// <summary>呼出threadの所属desktopを切り替えます。表示desktopは変更しません。</summary>
    /// <returns>接続成功です。</returns>
    [LibraryImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetThreadDesktop(IntPtr desktop);

    /// <summary>一時desktop handleを解放します。</summary>
    /// <returns>解放成功です。</returns>
    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseDesktop(IntPtr desktop);

    /// <summary>threadに所属する借用desktopを取得します。</summary>
    /// <returns>閉じてはいけない元desktop handleです。</returns>
    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr GetThreadDesktop(uint thread);

    /// <summary>所属desktopを調べるthread IDです。</summary>
    /// <returns>呼出threadのOS IDです。</returns>
    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetCurrentThreadId();
}
