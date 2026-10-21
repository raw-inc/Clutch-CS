using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Desktop;

namespace ReArtWord.Contents.BackgroundService.CoreService.Desktop.Clipboard;

/// <summary>対話workerのウィンドウステーション内で、Unicodeテキストだけを読み書きします。</summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsDesktopClipboard
{
    /// <summary>本文を取得せずにOSの更新番号だけを確認します。</summary>
    /// <returns>現在のクリップボード更新番号です。</returns>
    internal static uint Sequence() => GetClipboardSequenceNumber();

    /// <summary>文字形式以外は読み込まず、メモリ境界と終端を検証します。</summary>
    /// <returns>Unicodeテキスト、またはテキスト形式がなければnullです。</returns>
    internal static string? ReadText()
    {
        if (!OpenClipboard(IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        try
        {
            uint unicode = 13;
            if (!IsClipboardFormatAvailable(unicode))
            {
                return null;
            }
            IntPtr data = GetClipboardData(unicode);
            if (data == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            ulong bytes = GlobalSize(data).ToUInt64();
            if (bytes < 2)
            {
                throw new InvalidDataException("Clipboard.TextTooLarge");
            }
            IntPtr address = GlobalLock(data);
            if (address == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            try
            {
                int characters = checked((int)Math.Min(bytes / 2, (ulong)DesktopClipboardWire.MaximumTextBytes + 1));
                string text = Marshal.PtrToStringUni(address, characters);
                int end = text.IndexOf('\0', StringComparison.Ordinal);
                if (end < 0)
                {
                    throw new InvalidDataException("Clipboard.InvalidText");
                }
                return text[..end];
            }
            finally
            {
                _ = GlobalUnlock(data);
            }
        }
        finally
        {
            _ = CloseClipboard();
        }
    }

    /// <summary>全メモリを準備してから置換し、OSへ渡したメモリは二重解放しません。</summary>
    /// <returns>他アプリの使用中で、まだ変更していない場合だけfalseです。</returns>
    internal static bool TryWriteText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        char[] characters = (text + '\0').ToCharArray();
        IntPtr data = GlobalAlloc(2, new UIntPtr(checked((uint)characters.Length * 2)));
        if (data == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        IntPtr window = IntPtr.Zero;
        bool opened = false;
        try
        {
            IntPtr address = GlobalLock(data);
            if (address == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            try
            {
                Marshal.Copy(characters, 0, address, characters.Length);
            }
            finally
            {
                _ = GlobalUnlock(data);
            }

            // EmptyClipboardの所有者をNULLにせず、同じthread上の非表示windowへ関連付けます。
            window = CreateWindowExW(0, "STATIC", string.Empty, 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (window == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            opened = OpenClipboard(window);
            if (!opened)
            {
                return false;
            }
            if (!EmptyClipboard() || SetClipboardData(13, data) == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            data = IntPtr.Zero;
            return true;
        }
        finally
        {
            if (opened)
            {
                _ = CloseClipboard();
            }
            if (window != IntPtr.Zero)
            {
                _ = DestroyWindow(window);
            }
            if (data != IntPtr.Zero)
            {
                _ = GlobalFree(data);
            }
        }
    }

    /// <summary>OSの更新番号を取得します。</summary>
    /// <returns>更新番号です。</returns>
    [LibraryImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetClipboardSequenceNumber();

    /// <summary>同一thread内でクリップボードを排他取得します。</summary>
    /// <returns>取得できたかです。</returns>
    [LibraryImport("user32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(IntPtr window);

    /// <summary>クリップボードの排他を解放します。</summary>
    /// <returns>解放できたかです。</returns>
    [LibraryImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    /// <summary>読み取る形式の存在だけを確認します。</summary>
    /// <returns>形式が存在するかです。</returns>
    [LibraryImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsClipboardFormatAvailable(uint format);

    /// <summary>排他中に借用する本文handleを取得します。</summary>
    /// <returns>借用handleです。</returns>
    [LibraryImport("user32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr GetClipboardData(uint format);

    /// <summary>新しい本文を設定する直前に現在の内容を置換します。</summary>
    /// <returns>置換できたかです。</returns>
    [LibraryImport("user32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyClipboard();

    /// <summary>OSへ移動可能メモリの所有権を渡します。</summary>
    /// <returns>成功時のOS所有handleです。</returns>
    [LibraryImport("user32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr SetClipboardData(uint format, IntPtr data);

    /// <summary>OSへ渡せる移動可能メモリを準備します。</summary>
    /// <returns>呼出し元所有のhandleです。</returns>
    [LibraryImport("kernel32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    /// <summary>借用メモリの読取上限を取得します。</summary>
    /// <returns>確保済みバイト数です。</returns>
    [LibraryImport("kernel32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial UIntPtr GlobalSize(IntPtr data);

    /// <summary>handleから同期使用中のアドレスを取得します。</summary>
    /// <returns>借用アドレスです。</returns>
    [LibraryImport("kernel32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr GlobalLock(IntPtr data);

    /// <summary>同期使用を終了します。</summary>
    /// <returns>残っているロックの状態です。</returns>
    [LibraryImport("kernel32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(IntPtr data);

    /// <summary>OSへ所有権を渡していないメモリだけを解放します。</summary>
    /// <returns>成功時はNULLです。</returns>
    [LibraryImport("kernel32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr GlobalFree(IntPtr data);

    /// <summary>画面へ表示しない所有者windowを現在threadに作ります。</summary>
    /// <returns>所有するwindowです。</returns>
    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr CreateWindowExW(uint extended, string type, string name, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    /// <summary>同じthread上で所有者windowを解放します。</summary>
    /// <returns>破棄できたかです。</returns>
    [LibraryImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(IntPtr window);
}
