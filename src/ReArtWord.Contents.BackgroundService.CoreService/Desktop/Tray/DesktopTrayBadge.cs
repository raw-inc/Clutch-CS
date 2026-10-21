using System.Globalization;

namespace ReArtWord.Contents.BackgroundService.CoreService.Desktop.Tray;

/// <summary>外部画像やWindows Formsに依存せず、接続数入りmonitorアイコンを描きます。</summary>
internal static class DesktopTrayBadge
{
    private static readonly string[] digits = ["111101101101111", "010110010010111", "111001111100111", "111001111001111", "101101111001001",
        "111100111001111", "111100111101111", "111001001001001", "111101111101111", "111101111001111", "000010111010000"];

    /// <summary>小さい領域では99+、hoverでは正確な数を使います。</summary>
    /// <returns>アイコン内に表示する数字です。</returns>
    internal static string Label(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        return 99 < count ? "99+"
                          : count.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>32pxのBGRAと透過maskを返します。</summary>
    /// <returns>CreateIconへ渡す独立した配列です。</returns>
    internal static (byte[] Mask, byte[] Colors) Render(int count, bool controlling)
    {
        byte[] colors = new byte[32 * 32 * 4];
        byte[] mask = Enumerable.Repeat((byte)255, 32 * 4).ToArray();
        for (int y = 3; y < 25; y++)
        {
            for (int x = 1; x < 31; x++)
            {
                bool border = x < 3 || 28 < x || y < 5 || 22 < y;
                Paint(x, y, border, false, controlling, mask, colors);
            }
        }
        for (int y = 25; y < 30; y++)
        {
            int start = y < 28 ? 14
                               : 8;
            for (int x = start; x < 32 - start; x++)
            {
                Paint(x, y, true, false, controlling, mask, colors);
            }
        }
        string text = Label(count);
        int left = (32 - (text.Length * 8 - 2)) / 2;
        for (int character = 0; character < text.Length; character++)
        {
            int glyph = text[character] == '+' ? 10
                                               : text[character] - '0';
            for (int pixel = 0; pixel < 15; pixel++)
            {
                if (digits[glyph][pixel] != '1')
                {
                    continue;
                }
                for (int dy = 0; dy < 2; dy++)
                {
                    for (int dx = 0; dx < 2; dx++)
                    {
                        Paint(left + character * 8 + pixel % 3 * 2 + dx, 9 + pixel / 3 * 2 + dy, false, true, controlling, mask, colors);
                    }
                }
            }
        }
        string[] mark = controlling ? ["100000000", "110000000", "111000000", "111100000", "111110000", "111111000", "111111100", "110110000", "100011000"]
                                    : ["000000000", "000000000", "001111100", "011000110", "110010011", "011000110", "001111100", "000000000", "000000000"];
        for (int y = 0; y < mark.Length; y++)
        {
            for (int x = 0; x < mark[y].Length; x++)
            {
                Paint(21 + x, 22 + y, false, mark[y][x] == '1', controlling, mask, colors);
            }
        }
        return (mask, colors);
    }

    /// <summary>表示するpixelだけを不透明にし、数字には高いコントラストを使います。</summary>
    private static void Paint(int x, int y, bool border, bool number, bool controlling, byte[] mask, byte[] colors)
    {
        int offset = (y * 32 + x) * 4;
        int color = 0x1e2a37;
        if (border)
        {
            color = controlling ? 0xf5a438
                                : 0x6ca2cd;
        }
        if (number)
        {
            color = 0xfafafa;
        }
        colors[offset] = (byte)color;
        colors[offset + 1] = (byte)(color >> 8);
        colors[offset + 2] = (byte)(color >> 16);
        colors[offset + 3] = 255;
        mask[y * 4 + x / 8] &= (byte)~(128 >> (x % 8));
    }
}
