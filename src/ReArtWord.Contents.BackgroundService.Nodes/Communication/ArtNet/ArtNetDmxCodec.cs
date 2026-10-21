using System.Buffers.Binary;
using System.Text.Json;

namespace ReArtWord.Contents.BackgroundService.Nodes.Communication.ArtNet;

/// <summary>ArtDMXの固定ヘッダー、15bit Port-AddressとDMX長を検証します。</summary>
public static class ArtNetDmxCodec
{
    /// <summary>universe、sequence、physical、base64 データを符号化します。</summary>
    /// <returns>ArtDMXパケットです。</returns>
    public static byte[] Encode(JsonElement value)
    {
        int universe = value.GetProperty("universe").GetInt32();
        byte[] data = value.GetProperty("data").GetBytesFromBase64();
        if (universe is < 0 or > 32767 || data.Length is < 2 or > 512 || data.Length % 2 != 0)
        {
            throw new FormatException("ArtDMX universeまたは偶数データ長が不正です。");
        }
        byte[] packet = new byte[18 + data.Length];
        "Art-Net\0"u8.CopyTo(packet);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(8), 0x5000);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10), 14);
        string sequenceName = "sequence";
        if (value.TryGetProperty(sequenceName, out JsonElement sequence))
        {
            packet[12] = sequence.GetByte();
        }
        string physicalName = "physical";
        if (value.TryGetProperty(physicalName, out JsonElement physical))
        {
            packet[13] = physical.GetByte();
        }
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(14), checked((ushort)universe));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(16), checked((ushort)data.Length));
        data.CopyTo(packet, 18);
        return packet;
    }

    /// <summary>ArtDMX以外や欠損したパケットを拒否します。</summary>
    /// <returns>DMX値です。</returns>
    public static JsonElement Decode(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 18 || !packet[..8].SequenceEqual("Art-Net\0"u8) || BinaryPrimitives.ReadUInt16LittleEndian(packet[8..]) != 0x5000 || BinaryPrimitives.ReadUInt16BigEndian(packet[10..]) < 14)
        {
            throw new FormatException("ArtDMXヘッダーが不正です。");
        }
        int length = BinaryPrimitives.ReadUInt16BigEndian(packet[16..]);
        int universe = BinaryPrimitives.ReadUInt16LittleEndian(packet[14..]);
        if (length is < 2 or > 512 || length % 2 != 0 || packet.Length != length + 18 || 32767 < universe)
        {
            throw new FormatException("ArtDMXデータ長またはuniverseが不正です。");
        }
        return JsonSerializer.SerializeToElement(new { universe, sequence = packet[12], physical = packet[13], data = Convert.ToBase64String(packet[18..]) });
    }
}
