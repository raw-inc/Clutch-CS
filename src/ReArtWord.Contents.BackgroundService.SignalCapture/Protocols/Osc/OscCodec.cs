using System.Buffers.Binary;
using System.IO;
using System.Text;
using ReArtWord.Contents.BackgroundService.SignalCapture.Packets;
using ReArtWord.Contents.BackgroundService.SignalCapture.Sending;
using ReArtWord.Contents.BackgroundService.SignalCapture.Text;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.Protocols.Osc;

/// <summary>OSC の型付き引数と入れ子の Bundle を扱います。</summary>
internal static class OscCodec
{
    private static string bundleAddress => "#bundle";

    /// <summary>DNS の取引番号などを OSC と誤認しないため、型タグの位置まで確認します。</summary>
    /// <returns>OSC の識別可能なヘッダーがあるか。</returns>
    internal static bool HasMessageHeader(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith("#bundle\0"u8))
        {
            return true;
        }

        int end = data.IndexOf((byte)0);
        int types = (end + 4) & ~3;
        return data.StartsWith("/"u8) && 0 < end && types < data.Length && data[types] == ',';
    }

    /// <summary>OSC パケットを解析します。</summary>
    /// <returns>アドレス・型・値を含む表示。</returns>
    internal static string Decode(ReadOnlySpan<byte> data, int depth = 0, bool localize = false, OscPreview? preview = null)
    {
        if (8 < depth)
        {
            throw new FormatException("OSC Bundle nesting limit exceeded");
        }

        int offset = 0;
        string address = ReadString(data, ref offset);
        var result = new StringBuilder();
        if (string.Equals(address, bundleAddress, StringComparison.Ordinal))
        {
            FrameDecoder.Need(data, offset + 8);
            ulong timetag = BinaryPrimitives.ReadUInt64BigEndian(data[offset..]);
            result.AppendLine(string.Format(CultureInfo.InvariantCulture, localize ? UiText.Get("Bundle / Timetag: 0x{0:X16} (1 = immediate)")
                                                                                   : "Bundle / Timetag: 0x{0:X16} (1 = immediate)", timetag));
            offset += 8;
            while (offset < data.Length)
            {
                FrameDecoder.Need(data, offset + 4);
                int size = BinaryPrimitives.ReadInt32BigEndian(data[offset..]);
                offset += 4;
                if (size <= 0 || data.Length - offset < size)
                {
                    throw new FormatException("OSC Bundle element length");
                }

                result.AppendLine(Decode(data.Slice(offset, size), depth + 1, localize, preview));
                offset += size;
            }

            return result.ToString();
        }

        if (!address.StartsWith('/'))
        {
            throw new FormatException("OSC addresses must start with /");
        }

        string tags = ReadString(data, ref offset);
        if (!tags.StartsWith(','))
        {
            throw new FormatException("Missing OSC type tag");
        }

        result.AppendLine((localize ? UiText.Get("Path: ")
                                    : "Path: ") + DisplayText.Safe(address));
        result.AppendLine((localize ? UiText.Get("Types: ")
                                    : "Types: ") + DisplayText.Safe(tags));
        result.AppendLine((localize ? UiText.Get("Type names: ")
                                    : "Type names: ") + string.Join(", ", tags.AsSpan(1).ToArray().Select(tag => $"{tag}={TypeName(tag)}")));
        preview?.BeginMessage(address, 1 < tags.Length);
        foreach (char tag in tags.AsSpan(1))
        {
            string value = ReadValue(tag, data, ref offset);
            result.AppendLine(CultureInfo.InvariantCulture, $"  {tag}: {value}");
            preview?.AddValue(tag, value);
        }

        if (offset != data.Length)
        {
            throw new FormatException("OSC type tags do not match the data length");
        }

        return result.ToString();
    }

    /// <summary>OSC の型コードに対応する型名を表示します。</summary>
    /// <returns>規格上の型名。</returns>
    private static string TypeName(char tag) => tag switch
    {
        'i' => "int32",
        'f' => "float32",
        's' => "string",
        'b' => "blob",
        'h' => "int64",
        'd' => "float64",
        't' => "timetag",
        'S' => "symbol",
        'r' => "RGBA",
        'm' => "MIDI",
        'c' => "character",
        'T' or 'F' => "boolean",
        'N' => "nil",
        'I' => "impulse",
        _ => "unsupported"
    };

    /// <summary>一行ごとの型と値から OSC メッセージを組み立てます。</summary>
    /// <returns>ネットワーク順の OSC ペイロード。</returns>
    internal static byte[] Build(string address, string arguments)
    {
        if (!address.StartsWith('/') || address.Contains('\0', StringComparison.Ordinal))
        {
            throw new FormatException("OSC paths must start with / and must not contain NUL.");
        }

        using var values = new MemoryStream();
        var tags = new StringBuilder(",");
        foreach (string line in arguments.Replace("\r", "", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            char tag = line[0];
            string value = 1 < line.Length ? line[1..].TrimStart()
                                           : "";
            tags.Append(tag);
            var bytes = new byte[8];
            switch (tag)
            {
                case 'i':
                    BinaryPrimitives.WriteInt32BigEndian(bytes, int.Parse(value, CultureInfo.InvariantCulture));
                    values.Write(bytes, 0, 4);
                    break;
                case 'f':
                    BinaryPrimitives.WriteSingleBigEndian(bytes, float.Parse(value, CultureInfo.InvariantCulture));
                    values.Write(bytes, 0, 4);
                    break;
                case 's':
                    WriteString(values, value);
                    break;
                case 'b':
                    byte[] blob = PacketComposer.ParseHex(value);
                    BinaryPrimitives.WriteInt32BigEndian(bytes, blob.Length);
                    values.Write(bytes, 0, 4);
                    values.Write(blob);
                    Pad(values);
                    break;
                case 'T':
                case 'F':
                case 'N':
                case 'I':
                    if (value.Length != 0)
                    {
                        throw new FormatException("T/F/N/I do not take a value.");
                    }

                    break;
                default:
                    throw new FormatException("Supported compose types: i / f / s / b / T / F / N / I");
            }
        }

        using var result = new MemoryStream();
        WriteString(result, address);
        WriteString(result, tags.ToString());
        values.WriteTo(result);
        return result.ToArray();
    }

    /// <summary>型に対応する値を消費します。</summary>
    /// <returns>値の読みやすい表記。</returns>
    private static string ReadValue(char tag, ReadOnlySpan<byte> data, ref int offset)
    {
        if (tag is 'T' or 'F' or 'N' or 'I')
        {
            return tag switch
            {
                'T' => "true",
                'F' => "false",
                'N' => "nil",
                _ => "impulse"
            };
        }

        if (tag is 's' or 'S')
        {
            return DisplayText.Safe(ReadString(data, ref offset));
        }

        int count = tag is 'h' or 't' or 'd' ? 8
                                             : 4;
        FrameDecoder.Need(data, offset + count);
        ReadOnlySpan<byte> value = data.Slice(offset, count);
        offset += count;
        switch (tag)
        {
            case 'i':
                return BinaryPrimitives.ReadInt32BigEndian(value).ToString(CultureInfo.InvariantCulture);
            case 'f':
                return BinaryPrimitives.ReadSingleBigEndian(value).ToString("G9", CultureInfo.InvariantCulture);
            case 'h':
                return BinaryPrimitives.ReadInt64BigEndian(value).ToString(CultureInfo.InvariantCulture);
            case 'd':
                return BinaryPrimitives.ReadDoubleBigEndian(value).ToString("G17", CultureInfo.InvariantCulture);
            case 't':
                return "0x" + Convert.ToHexString(value);
            case 'r':
            case 'm':
            case 'c':
                return "0x" + Convert.ToHexString(value);
            case 'b':
                int length = BinaryPrimitives.ReadInt32BigEndian(value);
                if (length < 0 || data.Length - offset < length)
                {
                    throw new FormatException("OSC blob length");
                }

                string hex = Convert.ToHexString(data.Slice(offset, Math.Min(length, 128)));
                offset += length;
                offset = (offset + 3) & ~3;
                FrameDecoder.Need(data, offset);
                return $"{length} bytes: {hex}" + (128 < length ? "..."
                                                                : "");
            default:
                throw new FormatException($"Unsupported OSC type {tag}");
        }
    }

    /// <summary>NUL 終端と 4 バイト境界を検証します。</summary>
    /// <returns>OSC の文字列。</returns>
    private static string ReadString(ReadOnlySpan<byte> data, ref int offset)
    {
        FrameDecoder.Need(data, offset);
        int length = data[offset..].IndexOf((byte)0);
        if (length < 0)
        {
            throw new FormatException("OSC string termination");
        }

        string value = Encoding.UTF8.GetString(data.Slice(offset, length));
        int end = offset + length;
        offset = (end + 4) & ~3;
        FrameDecoder.Need(data, offset);
        for (int i = end; i < offset; i++)
        {
            if (data[i] != 0)
            {
                throw new FormatException("OSC padding");
            }
        }

        return value;
    }

    /// <summary>OSC 文字列を出力します。</summary>
    private static void WriteString(Stream stream, string value)
    {
        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new FormatException("Strings must not contain NUL.");
        }

        stream.Write(Encoding.UTF8.GetBytes(value));
        stream.WriteByte(0);
        Pad(stream);
    }

    /// <summary>4 バイト境界までゼロを補います。</summary>
    private static void Pad(Stream stream)
    {
        while (stream.Position % 4 != 0)
        {
            stream.WriteByte(0);
        }
    }
}
