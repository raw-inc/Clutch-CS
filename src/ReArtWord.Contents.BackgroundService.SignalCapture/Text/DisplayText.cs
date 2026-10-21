using System.Text;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.Text;

/// <summary>通信に含まれる制御文字を無害な表示へ変換します。</summary>
internal static class DisplayText
{
    /// <summary>端末制御文字をエスケープします。</summary>
    /// <returns>描画に利用できる文字列。</returns>
    internal static string Safe(string text)
    {
        var result = new StringBuilder();
        foreach (char value in text.Replace("\r\n", "\n", StringComparison.Ordinal))
        {
            if (char.IsControl(value) && value != '\n' && value != '\t')
            {
                result.Append(CultureInfo.InvariantCulture, $"\\x{(int)value:X2}");
            }
            else
            {
                result.Append(value);
            }
        }

        return result.ToString();
    }

    /// <summary>オフセットと ASCII を添えた Hex 表示を生成します。</summary>
    /// <returns>最大 4096 バイトの表示。</returns>
    internal static string Hex(ReadOnlySpan<byte> data)
    {
        var result = new StringBuilder();
        int length = Math.Min(data.Length, 4096);
        for (int offset = 0; offset < length; offset += 16)
        {
            result.Append(CultureInfo.InvariantCulture, $"{offset:X4}  ");
            int count = Math.Min(16, length - offset);
            for (int i = 0; i < 16; i++)
            {
                result.Append(i < count ? data[offset + i].ToString("X2", CultureInfo.InvariantCulture) + " "
                                        : "   ");
            }

            result.Append(' ');
            for (int i = 0; i < count; i++)
            {
                byte value = data[offset + i];
                result.Append(value is >= 32 and <= 126 ? (char)value
                                                        : '.');
            }

            result.AppendLine();
        }

        if (length < data.Length)
        {
            result.AppendLine(UiText.Get("Display limited to 4096 bytes. Dump saves all retained original data."));
        }

        return result.ToString();
    }
}
