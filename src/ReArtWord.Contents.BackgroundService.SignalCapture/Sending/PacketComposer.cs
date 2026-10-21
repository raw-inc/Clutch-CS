using System.Buffers.Binary;
using System.Net;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.Sending;

/// <summary>デモと試験の原本を構築し、ネットワークへは送信しません。</summary>
internal static class PacketComposer
{
    /// <summary>空白で区切った Hex を検証します。</summary>
    /// <returns>入力どおりのバイト列。</returns>
    internal static byte[] ParseHex(string text)
    {
        string compact = string.Concat(text.Where(value => !char.IsWhiteSpace(value)));
        if (131070 < compact.Length)
        {
            throw new FormatException("Hex data is limited to 65535 bytes.");
        }

        return Convert.FromHexString(compact);
    }

    /// <summary>デモと検証で使用する IPv4/UDP パケットを構築します。</summary>
    /// <returns>正しい IPv4 チェックサムを含むパケット。</returns>
    internal static byte[] UdpFrame(byte[] payload, string source, string destination, int port)
    {
        var data = new byte[28 + payload.Length];
        data[0] = 0x45;
        data[8] = 64;
        data[9] = 17;
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2), checked((ushort)data.Length));
        IPAddress.Parse(source).GetAddressBytes().CopyTo(data, 12);
        IPAddress.Parse(destination).GetAddressBytes().CopyTo(data, 16);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(20), 12000);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(22), checked((ushort)port));
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(24), checked((ushort)(payload.Length + 8)));
        uint sum = 0;
        for (int i = 0; i < 20; i += 2)
        {
            sum += BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(i));
        }

        while (65535 < sum)
        {
            sum = (sum & 65535) + (sum >> 16);
        }

        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(10), (ushort)~sum);
        payload.CopyTo(data, 28);
        return data;
    }
}
