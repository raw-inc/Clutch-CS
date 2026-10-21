using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.Packets;

/// <summary>観測点・L2・オフロード差を除き、IP 本文と識別値を維持したハッシュを生成します。</summary>
internal static class PacketFingerprint
{
    /// <summary>同じ IP 原本を異なるキャプチャ機構間で照合します。</summary>
    /// <returns>SHA-256 の小文字16進表現。</returns>
    internal static string Compute(ReadOnlySpan<byte> data, int linkType)
    {
        if (!IpPacketReader.TryRead(data, linkType, out IpPacketInfo ip))
        {
            var original = new byte[data.Length + 5];
            original[0] = 0;
            BinaryPrimitives.WriteInt32BigEndian(original.AsSpan(1), linkType);
            data.CopyTo(original.AsSpan(5));
            return Convert.ToHexStringLower(SHA256.HashData(original));
        }

        var normalized = new byte[ip.Length + 1];
        normalized[0] = (byte)ip.Version;
        data.Slice(ip.Offset, ip.Length).CopyTo(normalized.AsSpan(1));
        if (ip.Version == 4)
        {
            normalized[9] = 0;
            normalized[11] = 0;
            normalized[12] = 0;
        }
        else
        {
            normalized[8] = 0;
        }

        int checksum = ip.Transport switch
        {
            6 => 16,
            17 => 6,
            _ => -1
        };
        int position = ip.TransportOffset - ip.Offset + 1 + checksum;
        if (!ip.NonInitialFragment && 0 <= checksum && position + 1 < normalized.Length)
        {
            normalized[position] = 0;
            normalized[position + 1] = 0;
        }

        return Convert.ToHexStringLower(SHA256.HashData(normalized));
    }
}
