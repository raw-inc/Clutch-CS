using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ReArtWord.Contents.BackgroundService.CoreService.Desktop.Tray;

/// <summary>対話セッション内の非表示windowと、通知を出さないtray表示のOS境界です。</summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsTrayNative
{
    /// <summary>別workerが公開したicon識別子を、そのwindowの寿命内に参照します。</summary>
    /// <returns>取得した文字数です。</returns>
    internal static unsafe int GetWindowTextUnsafe(IntPtr window, char[] text, int maximum)
    {
        fixed (char* buffer = text)
        {
            return ReadWindowTextUnsafe(window, buffer, maximum);
        }
    }

    /// <summary>文字数で区切ったUTF-16領域へwindow名を書き込みます。</summary>
    /// <returns>終端を含まない文字数です。</returns>
    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial int ReadWindowTextUnsafe(IntPtr window, char* text, int maximum);

    /// <summary>所有thread上でwindow messageを受け取ります。</summary>
    /// <returns>Win32の処理結果です。</returns>
    internal delegate IntPtr WindowProcedure(IntPtr window, uint message, UIntPtr word, IntPtr parameter);

    /// <summary>window classのWin32配置です。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WindowClass
    {
        internal uint Size;
        internal uint Style;
        internal IntPtr Procedure;
        internal int ClassExtra;
        internal int WindowExtra;
        internal IntPtr Instance;
        internal IntPtr Icon;
        internal IntPtr Cursor;
        internal IntPtr Background;
        internal IntPtr Menu;
        internal string Name;
        internal IntPtr SmallIcon;

        /// <summary>診断にはclass名だけを使用します。</summary>
        /// <returns>class名です。</returns>
        public override readonly string ToString() => Name;
    }

    /// <summary>message loop用のWin32配置です。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Message
    {
        internal IntPtr Window;
        internal uint Id;
        internal UIntPtr Word;
        internal IntPtr Parameter;
        internal uint Time;
        internal int X;
        internal int Y;
        internal uint Private;

        /// <summary>message番号だけを診断へ渡します。</summary>
        /// <returns>message番号です。</returns>
        public override readonly string ToString() => Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>実行moduleの借用handleです。</summary>
    /// <returns>現在のmoduleです。</returns>
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial IntPtr GetModuleHandleW(string? name);

    /// <summary>プロセス内のclassを登録します。</summary>
    /// <returns>登録atomです。</returns>
    internal static unsafe ushort RegisterClassUnsafe(ref WindowClass definition)
    {
        Span<byte> storage = stackalloc byte[Marshal.SizeOf<WindowClass>()];
        storage.Clear();
        fixed (byte* buffer = storage)
        {
            int error = Marshal.GetLastPInvokeError();
            try
            {
                Marshal.StructureToPtr(definition, (IntPtr)buffer, false);
                ushort atom = RegisterWindowClass((IntPtr)buffer);
                error = Marshal.GetLastPInvokeError();
                return atom;
            }
            finally
            {
                Marshal.DestroyStructure<WindowClass>((IntPtr)buffer);
                Marshal.SetLastPInvokeError(error);
            }
        }
    }

    /// <summary>既存のUnicode構造体配置を維持した一時領域でclassを登録します。</summary>
    /// <returns>登録atomです。</returns>
    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial ushort RegisterWindowClass(IntPtr definition);

    /// <summary>表示・フォーカスを伴わないwindowを作ります。</summary>
    /// <returns>所有するwindowです。</returns>
    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial IntPtr CreateWindowExW(uint extendedStyle, string className, string name, uint style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    /// <summary>同じdesktop内の接続windowをZ順で参照します。</summary>
    /// <returns>一致したwindow、または0です。</returns>
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial IntPtr FindWindowExW(IntPtr parent, IntPtr after, string className, string? name);

    /// <summary>所有windowを同じthreadで破棄します。</summary>
    /// <returns>破棄成功です。</returns>
    [LibraryImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyWindow(IntPtr window);

    /// <summary>messageを待機します。</summary>
    /// <returns>0は終了、負数は取得失敗です。</returns>
    [LibraryImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int GetMessageW(out Message message, IntPtr window, uint minimum, uint maximum);

    /// <summary>window procedureへ配送します。</summary>
    /// <returns>処理結果です。</returns>
    [LibraryImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial IntPtr DispatchMessageW(ref Message message);

    /// <summary>対象外のmessageを既定処理します。</summary>
    /// <returns>既定処理結果です。</returns>
    [LibraryImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial IntPtr DefWindowProcW(IntPtr window, uint message, UIntPtr word, IntPtr parameter);

    /// <summary>別threadから終了だけを要求します。</summary>
    /// <returns>配送受付です。</returns>
    [LibraryImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessageW(IntPtr window, uint message, UIntPtr word, IntPtr parameter);

    /// <summary>低頻度の接続数確認を設定します。</summary>
    /// <returns>設定したtimerです。</returns>
    [LibraryImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial UIntPtr SetTimer(IntPtr window, UIntPtr id, uint milliseconds, IntPtr callback);

    /// <summary>所有threadのmessage loopを終了します。</summary>
    [LibraryImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial void PostQuitMessage(int result);

    /// <summary>Explorer再起動のmessageを解決します。</summary>
    /// <returns>登録message IDです。</returns>
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial uint RegisterWindowMessageW(string name);

    /// <summary>SYSTEMのwindowでもExplorer再起動通知だけを受けます。</summary>
    /// <returns>設定成功です。</returns>
    [LibraryImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ChangeWindowMessageFilterEx(IntPtr window, uint message, uint action, IntPtr change);

    /// <summary>実際に表示できた数をwindowへ公開します。</summary>
    /// <returns>設定成功です。</returns>
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetPropW(IntPtr window, string name, IntPtr value);

    /// <summary>他の接続windowの表示状態を参照します。</summary>
    /// <returns>公開値です。</returns>
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial IntPtr GetPropW(IntPtr window, string name);

    /// <summary>window破棄前に、この接続が追加したpropertyを取り除きます。</summary>
    /// <returns>削除した値です。</returns>
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial IntPtr RemovePropW(IntPtr window, string name);

    /// <summary>古い表示所有者がまだ存在するか確認します。</summary>
    /// <returns>有効なwindowかです。</returns>
    [LibraryImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindow(IntPtr window);

    /// <summary>iconを追加・更新・削除します。</summary>
    /// <returns>Shellが処理できたかです。</returns>
    internal static unsafe bool NotifyIconUnsafe(uint message, ref WindowsTrayIconData data)
    {
        Span<byte> storage = stackalloc byte[Marshal.SizeOf<WindowsTrayIconData>()];
        storage.Clear();
        fixed (byte* buffer = storage)
        {
            try
            {
                Marshal.StructureToPtr(data, (IntPtr)buffer, false);
                return NotifyIconNative(message, (IntPtr)buffer);
            }
            finally
            {
                Marshal.DestroyStructure<WindowsTrayIconData>((IntPtr)buffer);
            }
        }
    }

    /// <summary>固定長Unicode文字列を含むShell構造体を、既存のsizeと配置で渡します。</summary>
    /// <returns>Shellが処理できたかです。</returns>
    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool NotifyIconNative(uint message, IntPtr data);

    /// <summary>独自の数値付きmonitorアイコンを作ります。</summary>
    /// <returns>所有するHICONです。</returns>
    [LibraryImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial IntPtr CreateIcon(IntPtr instance, int width, int height, byte planes, byte bits, byte[] mask, byte[] colors);

    /// <summary>作成したHICONを解放します。</summary>
    /// <returns>解放成功です。</returns>
    [LibraryImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(IntPtr icon);
}
