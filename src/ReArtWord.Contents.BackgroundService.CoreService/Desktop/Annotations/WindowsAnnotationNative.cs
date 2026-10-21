using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop.Tray;

namespace ReArtWord.Contents.BackgroundService.CoreService.Desktop.Annotations;

/// <summary>capture対象外・非activate・click-throughの描画windowのOS境界です。</summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsAnnotationNative
{
    /// <summary>Win32 POINT/SIZEと同じ2整数配置です。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Pair(int x, int y)
    {
        internal int X = x;
        internal int Y = y;

        /// <summary>座標を診断します。</summary>
        /// <returns>二つの整数です。</returns>
        public override readonly string ToString() => FormattableString.Invariant($"{X},{Y}");
    }

    /// <summary>premultiplied alphaを指定するBLENDFUNCTIONです。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Blend
    {
        internal byte Operation;
        internal byte Flags;
        internal byte Alpha;
        internal byte Format;

        /// <summary>blendの不透明度を診断します。</summary>
        /// <returns>全体alphaです。</returns>
        public override readonly string ToString() => Alpha.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>32bit top-down DIBのheaderです。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfo
    {
        internal uint Size;
        internal int Width;
        internal int Height;
        internal ushort Planes;
        internal ushort Bits;
        internal uint Compression;
        internal uint ImageSize;
        internal int XResolution;
        internal int YResolution;
        internal uint Colors;
        internal uint Important;

        /// <summary>bitmapサイズを診断します。</summary>
        /// <returns>縦横pixelです。</returns>
        public override readonly string ToString() => FormattableString.Invariant($"{Width}x{Height}");
    }

    /// <summary>画面収録への二重描画を除外します。</summary>
    /// <returns>設定成功です。</returns>
    [LibraryImport("user32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowDisplayAffinity(IntPtr window, uint affinity);

    /// <summary>入力focusを変更せず表示位置とZ順を維持します。</summary>
    /// <returns>更新成功です。</returns>
    [LibraryImport("user32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    /// <summary>所有windowを非activate表示または非表示にします。</summary>
    /// <returns>以前の表示状態です。</returns>
    [LibraryImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(IntPtr window, int command);

    /// <summary>専用threadで保留messageを処理します。</summary>
    /// <returns>messageの有無です。</returns>
    [LibraryImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PeekMessageW(out WindowsTrayNative.Message message, IntPtr window, uint minimum, uint maximum, uint remove);

    /// <summary>一時bitmapのDCを作成します。</summary>
    /// <returns>所有DCです。</returns>
    [LibraryImport("gdi32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial IntPtr CreateCompatibleDC(IntPtr source);

    /// <summary>所有DCを解放します。</summary>
    /// <returns>解放成功です。</returns>
    [LibraryImport("gdi32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteDC(IntPtr source);

    /// <summary>top-down pixel領域を持つbitmapを作成します。</summary>
    /// <returns>所有bitmapです。</returns>
    [LibraryImport("gdi32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr pixels, IntPtr section, uint offset);

    /// <summary>bitmapをDCに選択します。</summary>
    /// <returns>元の選択objectです。</returns>
    [LibraryImport("gdi32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial IntPtr SelectObject(IntPtr dc, IntPtr value);

    /// <summary>DCから外したbitmapを解放します。</summary>
    /// <returns>解放成功です。</returns>
    [LibraryImport("gdi32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteObject(IntPtr value);

    /// <summary>位置と半透明pixelを一度に更新します。</summary>
    /// <returns>描画成功です。</returns>
    [LibraryImport("user32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UpdateLayeredWindow(IntPtr window, IntPtr target, ref Pair position, ref Pair size, IntPtr source, ref Pair origin, uint key, ref Blend blend, uint flags);
}
