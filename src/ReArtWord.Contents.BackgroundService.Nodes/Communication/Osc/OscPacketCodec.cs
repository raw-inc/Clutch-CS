using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Communication.Osc;

/// <summary>OSCメッセージとbundleを型タグと時刻を保って変換します。</summary>
public static class OscPacketCodec
{
    private static readonly UTF8Encoding strictUtf8 = new(false, true);

    /// <summary>address/argumentsまたはtimetag/elementsを持つJSONを符号化します。</summary>
    /// <returns>OSCパケットです。</returns>
    public static byte[] Encode(JsonElement packet)
    {
        using MemoryStream stream = new();
        WritePacket(stream, packet, 0);
        return stream.ToArray();
    }

    /// <summary>境界と型タグを検証し、アドレスだけで終わる旧形式は空引数として読み取ります。</summary>
    /// <returns>型タグを保ったJSONです。</returns>
    public static JsonElement Decode(ReadOnlySpan<byte> packet)
    {
        return JsonSerializer.SerializeToElement(ReadPacket(packet, 0));
    }

    /// <summary>OSC構造を再帰的に符号化します。</summary>
    private static void WritePacket(Stream stream, JsonElement packet, int depth)
    {
        if (16 < depth)
        {
            throw new FormatException("OSC bundleの入れ子が深すぎます。");
        }
        string bundleElements = "elements";
        if (packet.TryGetProperty(bundleElements, out JsonElement elements))
        {
            WriteString(stream, "#bundle");
            Span<byte> time = stackalloc byte[8];
            ulong timetag = packet.GetProperty("timetag").GetUInt64();
            BinaryPrimitives.WriteUInt64BigEndian(time, timetag);
            stream.Write(time);
            foreach (JsonElement element in elements.EnumerateArray())
            {
                using MemoryStream child = new();
                WritePacket(child, element, depth + 1);
                WriteInt(stream, checked((int)child.Length));
                child.Position = 0;
                child.CopyTo(stream);
            }
            return;
        }
        string address = packet.GetProperty("address").GetString() ?? string.Empty;
        if (!address.StartsWith('/'))
        {
            throw new FormatException("OSC addressは/で始めてください。");
        }
        WriteString(stream, address);
        JsonElement arguments = packet.GetProperty("arguments");
        string tags = "," + string.Concat(arguments.EnumerateArray().Select(argument => argument.GetProperty("type").GetString()));
        WriteString(stream, tags);
        foreach (JsonElement argument in arguments.EnumerateArray())
        {
            string type = argument.GetProperty("type").GetString() ?? string.Empty;
            JsonElement value = default;
            if (type.Length != 1 || type[0] is not ('T' or 'F' or 'N' or 'I'))
            {
                value = argument.GetProperty("value");
            }
            WriteArgument(stream, type, value);
        }
    }

    /// <summary>型ごとの値をネットワークバイト orderで書きます。</summary>
    private static void WriteArgument(Stream stream, string type, JsonElement value)
    {
        Span<byte> bytes = stackalloc byte[8];
        switch (type)
        {
            case "i":
                WriteInt(stream, value.GetInt32());
                break;
            case "f":
                float single = value.GetSingle();
                if (!float.IsFinite(single))
                {
                    throw new FormatException("OSCの32ビット浮動小数点数の範囲を超えています。");
                }
                WriteInt(stream, BitConverter.SingleToInt32Bits(single));
                break;
            case "s":
                WriteString(stream, value.GetString() ?? throw new FormatException("OSC文字列が必要です。"));
                break;
            case "b":
                byte[] blob = value.GetBytesFromBase64();
                WriteInt(stream, blob.Length);
                stream.Write(blob);
                WritePadding(stream, blob.Length);
                break;
            case "h":
                BinaryPrimitives.WriteInt64BigEndian(bytes, value.GetInt64());
                stream.Write(bytes);
                break;
            case "d":
                double number = value.GetDouble();
                if (!double.IsFinite(number))
                {
                    throw new FormatException("OSCの64ビット浮動小数点数の範囲を超えています。");
                }
                BinaryPrimitives.WriteInt64BigEndian(bytes, BitConverter.DoubleToInt64Bits(number));
                stream.Write(bytes);
                break;
            case "t":
                BinaryPrimitives.WriteUInt64BigEndian(bytes, value.GetUInt64());
                stream.Write(bytes);
                break;
            case "T":
            case "F":
            case "N":
            case "I":
                break;
            default:
                throw new FormatException("対応していないOSC型です。");
        }
    }

    /// <summary>長さ境界内で一つのOSC構造を解析します。</summary>
    /// <returns>検証済みの解析結果です。</returns>
    private static JsonObject ReadPacket(ReadOnlySpan<byte> packet, int depth)
    {
        if (16 < depth || packet.Length == 0 || packet.Length % 4 != 0)
        {
            throw new FormatException("OSCパケット長または入れ子が不正です。");
        }
        int offset = 0;
        string address = ReadString(packet, ref offset);
        string bundleAddress = "#bundle";
        if (string.Equals(address, bundleAddress, StringComparison.Ordinal))
        {
            ulong timetag = BinaryPrimitives.ReadUInt64BigEndian(Take(packet, ref offset, 8));
            JsonArray elements = new();
            while (offset < packet.Length)
            {
                int length = BinaryPrimitives.ReadInt32BigEndian(Take(packet, ref offset, 4));
                if (length <= 0)
                {
                    throw new FormatException("OSC bundle要素長が不正です。");
                }
                elements.Add(ReadPacket(Take(packet, ref offset, length), depth + 1));
            }
            return new JsonObject { ["timetag"] = timetag, ["elements"] = elements };
        }
        if (!address.StartsWith('/'))
        {
            throw new FormatException("OSC addressが不正です。");
        }

        // 旧送信実装の型タグ省略は、検証済みアドレスだけで終わる空引数メッセージに限定します。
        if (offset == packet.Length)
        {
            return new JsonObject { ["address"] = address, ["arguments"] = new JsonArray() };
        }
        string tags = ReadString(packet, ref offset);
        if (!tags.StartsWith(','))
        {
            throw new FormatException("OSC型タグがありません。");
        }
        JsonArray arguments = new();
        foreach (char tag in tags.AsSpan(1))
        {
            arguments.Add(ReadArgument(packet, ref offset, tag));
        }
        if (offset != packet.Length)
        {
            throw new FormatException("OSCパケットに余剰データがあります。");
        }
        return new JsonObject { ["address"] = address, ["arguments"] = arguments };
    }

    /// <summary>型ごとの引数を解析します。</summary>
    /// <returns>検証済みの解析結果です。</returns>
    private static JsonObject ReadArgument(ReadOnlySpan<byte> packet, ref int offset, char tag)
    {
        JsonNode? value;
        switch (tag)
        {
            case 'i':
                value = JsonValue.Create(BinaryPrimitives.ReadInt32BigEndian(Take(packet, ref offset, 4)));
                break;
            case 'f':
                value = JsonValue.Create(BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(Take(packet, ref offset, 4))));
                break;
            case 's':
                value = JsonValue.Create(ReadString(packet, ref offset));
                break;
            case 'b':
                int length = BinaryPrimitives.ReadInt32BigEndian(Take(packet, ref offset, 4));
                value = JsonValue.Create(Convert.ToBase64String(Take(packet, ref offset, length)));
                ReadPadding(packet, ref offset, length);
                break;
            case 'h':
                value = JsonValue.Create(BinaryPrimitives.ReadInt64BigEndian(Take(packet, ref offset, 8)));
                break;
            case 'd':
                value = JsonValue.Create(BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(Take(packet, ref offset, 8))));
                break;
            case 't':
                value = JsonValue.Create(BinaryPrimitives.ReadUInt64BigEndian(Take(packet, ref offset, 8)));
                break;
            case 'T':
                value = JsonValue.Create(true);
                break;
            case 'F':
                value = JsonValue.Create(false);
                break;
            case 'N':
                value = null;
                break;
            case 'I':
                value = null;
                break;
            default:
                throw new FormatException("対応していないOSC型です。");
        }
        return new JsonObject { ["type"] = tag.ToString(), ["value"] = value };
    }

    /// <summary>不足を拒否して指定長の領域を切り出します。</summary>
    /// <returns>検証済みの解析結果です。</returns>
    private static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> packet, ref int offset, int length)
    {
        if (length < 0 || packet.Length - offset < length)
        {
            throw new FormatException("OSCパケットが途中で切れています。");
        }
        ReadOnlySpan<byte> result = packet.Slice(offset, length);
        offset += length;
        return result;
    }

    /// <summary>UTF8と0埋めを検証して文字列を取得します。</summary>
    /// <returns>検証済みの解析結果です。</returns>
    private static string ReadString(ReadOnlySpan<byte> packet, ref int offset)
    {
        int length = packet[offset..].IndexOf((byte)0);
        if (length < 0)
        {
            throw new FormatException("OSC文字列に終端がありません。");
        }
        string value = strictUtf8.GetString(Take(packet, ref offset, length));
        _ = Take(packet, ref offset, 1);
        ReadPadding(packet, ref offset, length + 1);
        return value;
    }

    /// <summary>境界調整の0埋めを検証します。</summary>
    private static void ReadPadding(ReadOnlySpan<byte> packet, ref int offset, int length)
    {
        foreach (byte padding in Take(packet, ref offset, (4 - (length % 4)) % 4))
        {
            if (padding != 0)
            {
                throw new FormatException("OSC paddingは0である必要があります。");
            }
        }
    }

    /// <summary>終端と境界調整を含むOSC文字列を書きます。</summary>
    private static void WriteString(Stream stream, string value)
    {
        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new FormatException("OSC文字列にNULは指定できません。");
        }
        byte[] bytes = strictUtf8.GetBytes(value);
        stream.Write(bytes);
        stream.WriteByte(0);
        WritePadding(stream, bytes.Length + 1);
    }

    /// <summary>4byte境界まで0を書きます。</summary>
    private static void WritePadding(Stream stream, int length)
    {
        int count = (4 - (length % 4)) % 4;
        for (int index = 0; index < count; index++)
        {
            stream.WriteByte(0);
        }
    }

    /// <summary>32bit整数をbig endianで書きます。</summary>
    private static void WriteInt(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }
}
