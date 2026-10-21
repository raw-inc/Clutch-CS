using System.Runtime.Versioning;

namespace ReArtWord.Contents.BackgroundService.CoreService.Desktop.Tray;

/// <summary>一つのwindowだけが所有する、通知なしの接続数表示です。</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsDesktopTrayIcon(IntPtr window, Guid identity) : IDisposable
{
    private IntPtr icon;
    private int count;
    private int controlling;
    private bool shown;

    /// <summary>接続数・操作可能数の変化を反映し、Shellが未成立なら次回に再追加します。</summary>
    internal void Update(int connections, int controls)
    {
        if (shown && count == connections && controlling == controls)
        {
            return;
        }
        if (count != connections || (0 < controlling) != (0 < controls) || icon == IntPtr.Zero)
        {
            (byte[] mask, byte[] colors) = DesktopTrayBadge.Render(connections, 0 < controls);
            IntPtr next = WindowsTrayNative.CreateIcon(IntPtr.Zero, 32, 32, 1, 32, mask, colors);
            if (next == IntPtr.Zero)
            {
                return;
            }
            if (icon != IntPtr.Zero)
            {
                _ = WindowsTrayNative.DestroyIcon(icon);
            }
            icon = next;
            count = connections;
        }
        controlling = controls;
        WindowsTrayIconData data = WindowsTrayIconData.Create(window, icon, connections, controls, identity);
        uint operation = shown ? 1u
                               : 0u;
        bool updated = WindowsTrayNative.NotifyIconUnsafe(operation, ref data);
        if (!updated && operation == 0)
        {
            updated = WindowsTrayNative.NotifyIconUnsafe(1, ref data);
        }
        if (updated && !shown)
        {
            _ = WindowsTrayNative.NotifyIconUnsafe(4, ref data);
        }
        shown = updated;
        _ = WindowsTrayNative.SetPropW(window, DesktopTrayPresence.CountProperty, updated ? new IntPtr(connections)
                                                                                          : IntPtr.Zero);
        _ = WindowsTrayNative.SetPropW(window, DesktopTrayPresence.ControlCountProperty, updated ? new IntPtr(controls)
                                                                                                 : IntPtr.Zero);
    }

    /// <summary>Explorerの新しい通知領域へ次回更新で登録し直します。</summary>
    internal void Reset()
    {
        shown = false;
        _ = WindowsTrayNative.SetPropW(window, DesktopTrayPresence.CountProperty, IntPtr.Zero);
        _ = WindowsTrayNative.SetPropW(window, DesktopTrayPresence.ControlCountProperty, IntPtr.Zero);
    }

    /// <summary>古い所有windowの表示だけを取り除きます。</summary>
    internal static void Remove(IntPtr owner, Guid identity)
    {
        WindowsTrayIconData data = WindowsTrayIconData.Create(owner, IntPtr.Zero, 0, 0, identity);
        _ = WindowsTrayNative.NotifyIconUnsafe(2, ref data);
    }

    /// <summary>通知を出さずに表示とicon資源を解放します。</summary>
    internal void Hide()
    {
        if (shown)
        {
            Remove(window, identity);
            shown = false;
        }
        _ = WindowsTrayNative.SetPropW(window, DesktopTrayPresence.CountProperty, IntPtr.Zero);
        _ = WindowsTrayNative.SetPropW(window, DesktopTrayPresence.ControlCountProperty, IntPtr.Zero);
        if (icon != IntPtr.Zero)
        {
            _ = WindowsTrayNative.DestroyIcon(icon);
            icon = IntPtr.Zero;
        }
    }

    /// <summary>表示所有権とiconを解放します。</summary>
    public void Dispose() => Hide();
}
