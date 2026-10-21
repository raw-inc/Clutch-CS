using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ReArtWord.Contents.BackgroundService.Contracts.Desktop;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop.Capture;

namespace ReArtWord.Contents.BackgroundService.CoreService.Desktop.Input;

/// <summary>現在の入力desktopのカーソルを読み、共有system handleから標準種類を識別します。</summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsDesktopCursor(DesktopMonitor[] monitors)
{
    private static readonly (int Resource, EDesktopCursorShape Shape)[] standards =
    [
        (32512, EDesktopCursorShape.Default), (32513, EDesktopCursorShape.Text),
        (32514, EDesktopCursorShape.Wait), (32515, EDesktopCursorShape.Crosshair),
        (32642, EDesktopCursorShape.NorthWestSouthEast), (32643, EDesktopCursorShape.NorthEastSouthWest),
        (32644, EDesktopCursorShape.EastWest), (32645, EDesktopCursorShape.NorthSouth),
        (32646, EDesktopCursorShape.Move), (32648, EDesktopCursorShape.NotAllowed),
        (32649, EDesktopCursorShape.Link), (32650, EDesktopCursorShape.Progress), (32651, EDesktopCursorShape.Help),
    ];
    private IntPtr previous;
    private EDesktopCursorShape shape;

    /// <summary>awaitを挟まずdesktopを戻し、読取り不能時は古い種類を残しません。</summary>
    /// <returns>カーソルがある画面と種類です。</returns>
    internal (int Monitor, EDesktopCursorShape Shape) Read()
    {
        if (!WindowsInputDesktop.TryAttach(out WindowsInputDesktop scope))
        {
            return (0, EDesktopCursorShape.Default);
        }
        using (scope)
        {
            using DesktopDpiScope dpi = new();
            CursorInformation information = new() { Size = Marshal.SizeOf<CursorInformation>() };

            // 画面一覧は物理pixelなので、DPIが違う画面でも同じ座標系で照合します。
            if (!GetCursorInfo(ref information) || !GetPhysicalCursorPos(out information.Position))
            {
                return (0, EDesktopCursorShape.Default);
            }
            if (information.Handle != previous)
            {
                previous = information.Handle;
                shape = EDesktopCursorShape.Default;
                foreach ((int resource, EDesktopCursorShape candidate) in standards)
                {
                    // LoadCursorの共有handleは借用です。DestroyCursorを呼びません。
                    if (previous != IntPtr.Zero && previous == LoadCursorW(IntPtr.Zero, new IntPtr(resource)))
                    {
                        shape = candidate;
                        break;
                    }
                }
            }
            foreach (DesktopMonitor monitor in monitors)
            {
                if (monitor.Left <= information.Position.X && information.Position.X < (long)monitor.Left + monitor.Width
                    && monitor.Top <= information.Position.Y && information.Position.Y < (long)monitor.Top + monitor.Height)
                {
                    if ((information.Flags & 1) == 0)
                    {
                        return (monitor.Id, EDesktopCursorShape.Hidden);
                    }
                    return (monitor.Id, shape);
                }
            }
            return (0, EDesktopCursorShape.Default);
        }
    }

    /// <summary>Win32 CURSORINFOの固定配置です。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInformation
    {
        internal int Size;
        internal int Flags;
        internal IntPtr Handle;
        internal CursorPosition Position;

        /// <summary>native handleを含めず、状態と位置を説明します。</summary>
        /// <returns>取得したカーソル状態です。</returns>
        public override readonly string ToString() => FormattableString.Invariant($"Flags={Flags}, Position={Position}");
    }

    /// <summary>Win32 POINTの物理pixel座標です。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPosition
    {
        internal int X;
        internal int Y;

        /// <summary>取得した物理座標を説明します。</summary>
        /// <returns>XY座標です。</returns>
        public override readonly string ToString() => FormattableString.Invariant($"X={X}, Y={Y}");
    }

    /// <summary>他アプリを含む現在のカーソル状態を読みます。</summary>
    /// <returns>取得成功です。</returns>
    [LibraryImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorInfo(ref CursorInformation information);

    /// <summary>DPI仮想化されない位置を取得します。</summary>
    /// <returns>取得成功です。</returns>
    [LibraryImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPhysicalCursorPos(out CursorPosition position);

    /// <summary>Windows標準カーソルの借用handleを得ます。</summary>
    /// <returns>解放しない共有handleです。</returns>
    [LibraryImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr LoadCursorW(IntPtr instance, IntPtr resource);
}
