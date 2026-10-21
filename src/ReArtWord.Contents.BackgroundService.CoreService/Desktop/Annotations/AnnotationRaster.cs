using System.Diagnostics.CodeAnalysis;
using ReArtWord.Contents.BackgroundService.Contracts.Desktop;

namespace ReArtWord.Contents.BackgroundService.CoreService.Desktop.Annotations;

/// <summary>操作を受け取らないlayered window用のpremultiplied BGRA描画です。</summary>
internal sealed class AnnotationRaster
{
    // 抑制理由: 描画範囲が変わった時だけ再確保し、同寸法のフレーム間ではbufferを再利用します。
    [SuppressMessage("SourceQuality", "RAW170", Justification = "monitor全体の常時確保を避け、描画範囲に合わせてresizeします。")]
    private byte[] pixels = [];

    /// <summary>実際に描画する物理pixelの左上です。</summary>
    internal int Left
    {
        get; private set;
    }

    /// <summary>実際に描画する物理pixelの左上です。</summary>
    internal int Top
    {
        get; private set;
    }

    /// <summary>有限bufferの幅です。</summary>
    internal int Width
    {
        get; private set;
    }

    /// <summary>有限bufferの高さです。</summary>
    internal int Height
    {
        get; private set;
    }

    /// <summary>UpdateLayeredWindowが要求するpremultiplied BGRAです。</summary>
    internal byte[] Pixels => pixels;

    /// <summary>描画座標ごとの経過時間から、線を段階的に消します。</summary>
    /// <returns>表示対象が存在するかです。</returns>
    internal bool Render(DesktopMonitor monitor, DesktopAnnotationSnapshot snapshot)
    {
        List<(long[] Start, long[] End)> segments = [];
        Dictionary<(long Color, long Line), long[]> previous = [];
        int left = monitor.Left + monitor.Width, top = monitor.Top + monitor.Height, right = monitor.Left, bottom = monitor.Top;
        void Include(long x1, long y1, long x2, long y2)
        {
            int x = (int)Math.Clamp(Math.Min(x1, x2) - 4, monitor.Left, monitor.Left + monitor.Width);
            int y = (int)Math.Clamp(Math.Min(y1, y2) - 4, monitor.Top, monitor.Top + monitor.Height);
            int r = (int)Math.Clamp(Math.Max(x1, x2) + 24, monitor.Left, monitor.Left + monitor.Width);
            int b = (int)Math.Clamp(Math.Max(y1, y2) + 28, monitor.Top, monitor.Top + monitor.Height);
            if (x < r && y < b)
            {
                left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, r); bottom = Math.Max(bottom, b);
            }
        }
        foreach (long[] point in snapshot.Points)
        {
            (long Color, long Line) key = (point[1], point[2]);
            long[] start = previous.GetValueOrDefault(key) ?? point;
            segments.Add((start, point));
            Include(start[3], start[4], point[3], point[4]);
            previous[key] = point;
        }
        foreach (long[] cursor in snapshot.Cursors)
        {
            Include(cursor[1], cursor[2], cursor[1], cursor[2]);
        }
        if (right <= left || bottom <= top)
        {
            return false;
        }
        Left = left; Top = top; Width = right - left; Height = bottom - top;
        int count = checked(Width * Height * 4);
        if (64 * 1024 * 1024 < count)
        {
            return false;
        }
        if (Pixels.Length != count)
        {
            pixels = new byte[count];
        }
        else
        {
            Array.Clear(Pixels);
        }
        foreach ((long[] start, long[] end) in segments)
        {
            Line(start, end, snapshot.Now);
        }
        foreach (long[] cursor in snapshot.Cursors)
        {
            int x = (int)cursor[1] - Left, y = (int)cursor[2] - Top, color = (int)cursor[0];
            for (int row = 0; row < 23; row++)
            {
                for (int column = 0; column <= row * 0.65; column++)
                {
                    if (row < 16 || column < 5 || row - column < 12)
                    {
                        Pixel(x + column, y + row, color, 165);
                    }
                }
            }
        }
        return true;
    }

    /// <summary>点間を補間し、各座標の作成時間に対応したalphaを使います。</summary>
    private void Line(long[] start, long[] end, long now)
    {
        double dx = end[3] - start[3], dy = end[4] - start[4];
        int steps = (int)Math.Min(32768, Math.Max(1, Math.Ceiling(Math.Max(Math.Abs(dx), Math.Abs(dy)))));
        for (int index = 0; index <= steps; index++)
        {
            double part = (double)index / steps;
            int x = (int)Math.Round(start[3] + dx * part) - Left, y = (int)Math.Round(start[4] + dy * part) - Top;
            int alpha = (int)(150 * Math.Clamp(1 - (now - start[5] - (end[5] - start[5]) * part) / DesktopAnnotationSnapshot.Duration, 0, 1));
            for (int row = -2; row <= 2; row++)
            {
                for (int column = -2; column <= 2; column++)
                {
                    if (column * column + row * row <= 5)
                    {
                        Pixel(x + column, y + row, (int)end[1], alpha);
                    }
                }
            }
        }
    }

    /// <summary>線の重なる同色pixelを加算せず、半透明を維持します。</summary>
    private void Pixel(int x, int y, int color, int alpha)
    {
        if (x < 0 || y < 0 || Width <= x || Height <= y || alpha == 0)
        {
            return;
        }
        int offset = (y * Width + x) * 4;
        if (alpha < Pixels[offset + 3])
        {
            return;
        }
        Pixels[offset] = (byte)((color & 255) * alpha / 255);
        Pixels[offset + 1] = (byte)((color >> 8 & 255) * alpha / 255);
        Pixels[offset + 2] = (byte)((color >> 16 & 255) * alpha / 255);
        Pixels[offset + 3] = (byte)alpha;
    }
}
