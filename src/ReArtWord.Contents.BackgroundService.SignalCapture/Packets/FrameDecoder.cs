using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.SignalCapture.Protocols;
using ReArtWord.Contents.BackgroundService.SignalCapture.Text;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.Packets;

/// <summary>リンク層・IP・UDP・TCP の境界を検証して抽出します。</summary>
internal static class FrameDecoder
{
    /// <summary>原本を保持したまま表示情報を解析します。</summary>
    /// <returns>未知または不完全なパケットも含む解析結果。</returns>
    internal static PacketRecord Decode(byte[] data, int linkType, DateTimeOffset time, int originalLength = 0)
    {
        var packet = new PacketRecord
        {
            Data = data,
            LinkType = linkType,
            Time = time,
            OriginalLength = Math.Max(data.Length, originalLength)
        };
        Decode(packet);
        return packet;
    }

    /// <summary>観測情報を保持したまま解析結果を追加します。</summary>
    internal static void Decode(PacketRecord packet)
    {
        packet.Value = "";
        packet.NetworkFamily = AddressFamily.Unspecified;
        try
        {
            DecodeLayers(packet);
            if (packet.Data.Length < packet.OriginalLength)
            {
                packet.Details += "\nThe captured length is shorter than the original; data is missing.";
            }
        }
        catch (FormatException exception)
        {
            packet.Summary = "Incomplete / malformed: " + exception.Message;
            packet.Details = packet.Summary;
            packet.PayloadLength = 0;
            packet.ContentProtocol = "";
            packet.Value = ValuePreview.Hex(packet.Data);
        }
    }

    /// <summary>リンク種別ごとに IP ヘッダーの位置を求めます。</summary>
    private static void DecodeLayers(PacketRecord packet)
    {
        ReadOnlySpan<byte> data = packet.Data;
        int offset;
        int type;
        switch (packet.LinkType)
        {
            case 1:
                Need(data, 14);
                packet.Destination = Convert.ToHexString(data[..6]);
                packet.Source = Convert.ToHexString(data.Slice(6, 6));
                type = BinaryPrimitives.ReadUInt16BigEndian(data[12..]);
                offset = 14;
                int tags = 0;
                while (type is 0x8100 or 0x88a8 && tags++ < 4)
                {
                    Need(data, offset + 4);
                    type = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]);
                    offset += 4;
                }

                break;
            case 0:
            case 108:
                Need(data, 5);
                offset = 4;
                type = (data[4] >> 4) == 6 ? 0x86dd
                                           : 0x0800;
                break;
            case 12:
            case 101:
            case 228:
            case 229:
                Need(data, 1);
                offset = 0;
                type = (data[0] >> 4) == 6 ? 0x86dd
                                           : 0x0800;
                break;
            case 113:
                Need(data, 16);
                offset = 16;
                type = BinaryPrimitives.ReadUInt16BigEndian(data[14..]);
                break;
            case 276:
                Need(data, 20);
                offset = 20;
                type = BinaryPrimitives.ReadUInt16BigEndian(data);
                break;
            default:
                packet.Summary = $"Unsupported link type {packet.LinkType}";
                packet.Value = ValuePreview.Hex(data);
                return;
        }

        if (type == 0x0800)
        {
            DecodeIpv4(packet, offset);
        }
        else
        {
            if (type == 0x86dd)
            {
                DecodeIpv6(packet, offset);
            }
            else
            {
                if (type == 0x0806)
                {
                    ArpDecoder.Decode(packet, offset);
                    return;
                }

                packet.Protocol = "Ethernet";
                packet.PayloadOffset = offset;
                packet.PayloadLength = data.Length - offset;
                packet.Value = ValuePreview.Hex(data[offset..]);
                packet.Summary = $"EtherType 0x{type:X4}";
                packet.Details = packet.Summary;
            }
        }
    }

    /// <summary>IPv4 の長さとフラグメントを検証します。</summary>
    private static void DecodeIpv4(PacketRecord packet, int offset)
    {
        ReadOnlySpan<byte> data = packet.Data;
        Need(data, offset + 20);
        int header = (data[offset] & 15) * 4;
        int total = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]);
        if (data[offset] >> 4 != 4 || header < 20 || total < header)
        {
            throw new FormatException("IPv4 header length");
        }

        packet.NetworkFamily = AddressFamily.InterNetwork;
        Need(data, offset + total);
        packet.Source = new IPAddress(data.Slice(offset + 12, 4)).ToString();
        packet.Destination = new IPAddress(data.Slice(offset + 16, 4)).ToString();
        packet.Protocol = "IPv4";
        if ((BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 6)..]) & 0x3fff) != 0)
        {
            packet.Summary = "IP fragment (reassembly unsupported)";
            packet.Details = packet.Summary;
            packet.Value = ValuePreview.Hex(data.Slice(offset + header, total - header));
            return;
        }

        DecodeTransport(packet, offset + header, offset + total, data[offset + 9]);
    }

    /// <summary>IPv6 と代表的な拡張ヘッダーを読み取ります。</summary>
    private static void DecodeIpv6(PacketRecord packet, int offset)
    {
        ReadOnlySpan<byte> data = packet.Data;
        Need(data, offset + 40);
        if (data[offset] >> 4 != 6)
        {
            throw new FormatException("IPv6 version");
        }

        packet.NetworkFamily = AddressFamily.InterNetworkV6;
        int end = offset + 40 + BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 4)..]);
        Need(data, end);
        packet.Source = new IPAddress(data.Slice(offset + 8, 16)).ToString();
        packet.Destination = new IPAddress(data.Slice(offset + 24, 16)).ToString();
        packet.Protocol = "IPv6";
        int next = data[offset + 6];
        offset += 40;
        int extensions = 0;
        while (next is 0 or 43 or 60 or 51)
        {
            Need(data[..end], offset + 2);
            int length = next == 51 ? (data[offset + 1] + 2) * 4
                                    : (data[offset + 1] + 1) * 8;
            next = data[offset];
            offset += length;
            if (16 < ++extensions)
            {
                throw new FormatException("Too many IPv6 extension headers");
            }
        }

        Need(data[..end], offset);
        if (next == 44)
        {
            packet.Summary = "IPv6 fragment (reassembly unsupported)";
            packet.Details = packet.Summary;
            packet.Value = ValuePreview.Hex(data[offset..end]);
            return;
        }

        DecodeTransport(packet, offset, end, next, true);
    }

    /// <summary>UDP または TCP の上位層を切り出します。</summary>
    private static void DecodeTransport(PacketRecord packet, int offset, int end, int protocol, bool ipv6 = false)
    {
        ReadOnlySpan<byte> data = packet.Data.AsSpan(0, end);
        packet.Summary = $"IP protocol {protocol}";
        if (!ipv6 && protocol == 1 || ipv6 && protocol == 58)
        {
            IcmpDecoder.Decode(packet, offset, end, ipv6);
            return;
        }

        if (protocol is not (6 or 17))
        {
            packet.Details = packet.Summary;
            packet.Value = ValuePreview.Hex(data[offset..]);
            return;
        }

        Need(data, offset + 8);
        packet.SourcePort = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
        packet.DestinationPort = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]);
        if (protocol == 17)
        {
            int length = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 4)..]);
            if (length < 8)
            {
                throw new FormatException("UDP length");
            }

            Need(data, offset + length);
            packet.Transport = "UDP";
            packet.PayloadOffset = offset + 8;
            packet.PayloadLength = length - 8;
        }
        else
        {
            Need(data, offset + 20);
            int length = (data[offset + 12] >> 4) * 4;
            if (length < 20)
            {
                throw new FormatException("TCP header length");
            }

            Need(data, offset + length);
            packet.Transport = "TCP";
            packet.PayloadOffset = offset + length;
            packet.PayloadLength = end - packet.PayloadOffset;
            packet.Sequence = BinaryPrimitives.ReadUInt32BigEndian(data[(offset + 4)..]);
            packet.TcpFlags = data[offset + 13];
        }

        packet.Protocol = packet.Transport;
        packet.Summary = $"{packet.PayloadLength} bytes";
        packet.Details = $"{packet.Transport}\n{packet.Source}:{packet.SourcePort} -> {packet.Destination}:{packet.DestinationPort}\nPayload: {packet.PayloadLength} bytes\nTCP flags: 0x{packet.TcpFlags:X2}";
        if (protocol == 17)
        {
            ProtocolDecoder.Decode(packet);
        }

        if (string.Equals(packet.Protocol, packet.Transport, StringComparison.Ordinal))
        {
            packet.Value = ValuePreview.Hex(data.Slice(packet.PayloadOffset, packet.PayloadLength));
        }
    }

    /// <summary>宣言された範囲がキャプチャ内にあることを確認します。</summary>
    internal static void Need(ReadOnlySpan<byte> data, int length)
    {
        if (length < 0 || data.Length < length)
        {
            throw new FormatException("Packet is too short");
        }
    }
}
