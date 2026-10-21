using System.Runtime.InteropServices;

namespace ReArtWord.Contents.BackgroundService.CoreService.Desktop.Tray;

/// <summary>Shellの通知領域へ渡すUnicode版の構造体です。通知本文は常に空です。</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WindowsTrayIconData
{
    internal uint Size;
    internal IntPtr Window;
    internal uint Id;
    internal uint Flags;
    internal uint Callback;
    internal IntPtr Icon;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Tip;
    internal uint State;
    internal uint StateMask;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Information;
    internal uint Version;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string InformationTitle;
    internal uint InformationFlags;
    internal Guid Guid;
    internal IntPtr BalloonIcon;

    /// <summary>通知を要求せず、アイコンと通常のhover説明だけを更新します。</summary>
    /// <returns>NIF_INFOや通知音を含まないデータです。</returns>
    internal static WindowsTrayIconData Create(IntPtr window, IntPtr icon, int count, int controlling, Guid identity = default) => new()
    {
        Size = (uint)Marshal.SizeOf<WindowsTrayIconData>(), Window = window, Id = 1, Icon = icon, Flags = FlagsFor(identity), Guid = identity,
        Tip = FormattableString.Invariant($"Clutch-CS Remote Desktop · Total/接続数: {count} · Control/操作可能: {controlling} · View/未操作: {count - controlling}"),
        Information = string.Empty, InformationTitle = string.Empty, Version = 4,
    };

    /// <summary>旧workerの回収だけはHWND識別へ戻し、新表示は安定GUIDを使います。</summary>
    /// <returns>通知本文を含まないShellフラグです。</returns>
    private static uint FlagsFor(Guid identity)
    {
        uint flags = 0x02 | 0x04 | 0x80;
        if (identity != System.Guid.Empty)
        {
            flags |= 0x20;
        }
        return flags;
    }
}
