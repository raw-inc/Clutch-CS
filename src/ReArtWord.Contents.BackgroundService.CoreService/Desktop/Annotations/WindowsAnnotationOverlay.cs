using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ReArtWord.Contents.BackgroundService.Contracts.Desktop;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop.Tray;

namespace ReArtWord.Contents.BackgroundService.CoreService.Desktop.Annotations;

/// <summary>一つのmonitor上で、入力を奪わずcaptureにも入らない指示表示を所有します。</summary>
// 抑制理由: windowは生成thread上でのみ破棄でき、専用threadのfinallyが全資源を回収します。
[SupportedOSPlatform("windows"), SuppressMessage("Usage", "CA2216", Justification = "finalizer threadからwindowを破棄できません。表示threadのfinallyで必ずDisposeします。")]
internal sealed class WindowsAnnotationOverlay : IDisposable
{
    private static readonly WindowsTrayNative.WindowProcedure procedure = Dispatch;
    private static readonly Lazy<ushort> registered = new(Register);
    private readonly AnnotationRaster raster = new();
    private readonly IntPtr window;
    private readonly IntPtr dc;
    private IntPtr bitmap;
    private IntPtr previous;
    private IntPtr pixels;
    private int width;
    private int height;

    /// <summary>native検証と識別のためのwindow class名です。</summary>
    internal static string ClassName => "ReArtWord.Contents.RemoteDesktop.Annotations.v1";

    /// <summary>フォーカスを取らず、クリックを下のアプリへ通すtop-level windowを作ります。</summary>
    internal WindowsAnnotationOverlay()
    {
        _ = registered.Value;
        window = WindowsTrayNative.CreateWindowExW(0x080800a8, ClassName, "Clutch-CS viewer annotations", 0x80000000, 0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero, WindowsTrayNative.GetModuleHandleW(null), IntPtr.Zero);
        if (window == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) || !WindowsAnnotationNative.SetWindowDisplayAffinity(window, 0x11))
        {
            _ = WindowsTrayNative.DestroyWindow(window);
            throw new InvalidOperationException("Capture-excluded annotations require Windows 10 version 2004 or later with desktop composition.");
        }
        dc = WindowsAnnotationNative.CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero)
        {
            _ = WindowsTrayNative.DestroyWindow(window);
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    /// <summary>表示担当だけが最新座標を描画し、他のworkerはwindowを隠します。</summary>
    internal void Update(DesktopMonitor monitor, DesktopAnnotationSnapshot snapshot)
    {
        if (!snapshot.Owner || !raster.Render(monitor, snapshot))
        {
            Hide();
            return;
        }
        if (width != raster.Width || height != raster.Height)
        {
            ReleaseBitmap();
            WindowsAnnotationNative.BitmapInfo info = new() { Size = 40, Width = raster.Width, Height = -raster.Height, Planes = 1, Bits = 32 };
            bitmap = WindowsAnnotationNative.CreateDIBSection(dc, ref info, 0, out pixels, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            previous = WindowsAnnotationNative.SelectObject(dc, bitmap);
            width = raster.Width; height = raster.Height;
        }
        Marshal.Copy(raster.Pixels, 0, pixels, raster.Pixels.Length);
        WindowsAnnotationNative.Pair position = new(raster.Left, raster.Top), size = new(width, height), origin = new(0, 0);
        WindowsAnnotationNative.Blend blend = new() { Alpha = 255, Format = 1 };
        if (!WindowsAnnotationNative.UpdateLayeredWindow(window, IntPtr.Zero, ref position, ref size, dc, ref origin, 0, ref blend, 2))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        _ = WindowsAnnotationNative.SetWindowPos(window, new IntPtr(-1), 0, 0, 0, 0, 0x53);
    }

    /// <summary>owner喪失や一時read競合の際に残像を残しません。</summary>
    internal void Hide() => WindowsAnnotationNative.ShowWindow(window, 0);

    /// <summary>bitmapをDCから外してから解放します。</summary>
    private void ReleaseBitmap()
    {
        if (bitmap != IntPtr.Zero)
        {
            _ = WindowsAnnotationNative.SelectObject(dc, previous);
            _ = WindowsAnnotationNative.DeleteObject(bitmap);
            bitmap = IntPtr.Zero;
        }
    }

    /// <summary>所属thread上でwindowとGDI資源を回収します。</summary>
    public void Dispose()
    {
        ReleaseBitmap();
        _ = WindowsAnnotationNative.DeleteDC(dc);
        _ = WindowsTrayNative.DestroyWindow(window);
    }

    /// <summary>messageは入力を受け取らず、その他は既定処理へ渡します。</summary>
    /// <returns>HTTRANSPARENTまたはMA_NOACTIVATEです。</returns>
    private static IntPtr Dispatch(IntPtr window, uint message, UIntPtr word, IntPtr parameter) => message switch
    {
        0x0084 => new IntPtr(-1),
        0x0021 => new IntPtr(3),
        _ => WindowsTrayNative.DefWindowProcW(window, message, word, parameter),
    };

    /// <summary>pointer先のdelegateをプロセス寿命中保持します。</summary>
    /// <returns>登録atomです。</returns>
    private static ushort Register()
    {
        WindowsTrayNative.WindowClass definition = new()
        {
            Size = (uint)Marshal.SizeOf<WindowsTrayNative.WindowClass>(), Name = ClassName,
            Instance = WindowsTrayNative.GetModuleHandleW(null), Procedure = Marshal.GetFunctionPointerForDelegate(procedure),
        };
        ushort atom = WindowsTrayNative.RegisterClassUnsafe(ref definition);
        if (atom == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return atom;
    }
}
