using System.Buffers.Binary;
using System.Net;
using System.Text;
using ReArtWord.Contents.BackgroundService.SignalCapture.Packets;
using ReArtWord.Contents.BackgroundService.SignalCapture.Text;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.Protocols;

/// <summary>ICMP/ICMPv6 の制御種別と Echo・近隣探索を表示します。</summary>
internal static class IcmpDecoder
{
    /// <summary>IP アドレスを維持して制御メッセージを解析します。</summary>
    internal static void Decode(PacketRecord packet, int offset, int end, bool ipv6)
    {
        packet.Protocol = ipv6 ? "ICMPv6"
                               : "ICMP";
        ReadOnlySpan<byte> data = packet.Data.AsSpan(offset, end - offset);
        FrameDecoder.Need(data, 4);
        int type = data[0];
        int code = data[1];
        string name = Name(type, ipv6);
        int header = 4;
        var details = new StringBuilder();
        details.AppendLine(CultureInfo.InvariantCulture, $"{packet.Protocol} {name}\nType: {type}\nCode: {code}\nChecksum: 0x{BinaryPrimitives.ReadUInt16BigEndian(data[2..]):X4}");
        bool echo = ipv6 ? type is 128 or 129
                         : type is 0 or 8;
        if (echo)
        {
            FrameDecoder.Need(data, 8);
            header = 8;
            details.AppendLine(CultureInfo.InvariantCulture, $"Identifier: {BinaryPrimitives.ReadUInt16BigEndian(data[4..])}\nSequence: {BinaryPrimitives.ReadUInt16BigEndian(data[6..])}");
        }
        else
        {
            if (ipv6 && type is 133 or 134 or 135 or 136 or 137)
            {
                header = type switch
                {
                    133 => 8,
                    134 => 16,
                    137 => 40,
                    _ => 24
                };
                FrameDecoder.Need(data, header);
                if (type is 135 or 136 or 137)
                {
                    details.AppendLine("Target address: " + new IPAddress(data.Slice(8, 16)));
                }

                if (type == 137)
                {
                    details.AppendLine("Redirect destination: " + new IPAddress(data.Slice(24, 16)));
                }

                AppendOptions(data, header, details);
            }
            else
            {
                if (ipv6 && type is 1 or 2 or 3 or 4 || !ipv6 && type is 3 or 4 or 5 or 11 or 12 or 13 or 14 or 17 or 18)
                {
                    header = ipv6 ? 8
                                  : type switch
                    {
                        13 or 14 => 20,
                        17 or 18 => 12,
                        _ => 8
                    };
                    FrameDecoder.Need(data, header);
                    if (ipv6 && type == 2)
                    {
                        details.AppendLine(CultureInfo.InvariantCulture, $"MTU: {BinaryPrimitives.ReadUInt32BigEndian(data[4..])}");
                    }

                    if (!ipv6 && type == 3 && code == 4)
                    {
                        details.AppendLine(CultureInfo.InvariantCulture, $"MTU: {BinaryPrimitives.ReadUInt16BigEndian(data[6..])}");
                    }
                }
            }
        }

        packet.PayloadOffset = offset + header;
        packet.PayloadLength = data.Length - header;
        packet.Value = ValuePreview.Hex(data[header..]);
        packet.Summary = $"{name} / Type={type} Code={code}";
        packet.Details = details.ToString();
    }

    /// <summary>近隣探索のオプション長を検証し、表示量を制限します。</summary>
    private static void AppendOptions(ReadOnlySpan<byte> data, int offset, StringBuilder details)
    {
        int count = 0;
        while (offset < data.Length)
        {
            FrameDecoder.Need(data, offset + 2);
            int length = data[offset + 1] * 8;
            if (length == 0)
            {
                throw new FormatException("ICMPv6 option length is zero.");
            }

            FrameDecoder.Need(data, offset + length);
            if (count++ < 16)
            {
                details.AppendLine(CultureInfo.InvariantCulture, $"Option {data[offset]}: {ValuePreview.Hex(data.Slice(offset + 2, length - 2))}");
            }

            offset += length;
        }

        if (16 < count)
        {
            details.AppendLine("...");
        }
    }

    /// <summary>IP バージョンごとの制御種別を返します。</summary>
    /// <returns>種別名。未知の値は数値も維持します。</returns>
    private static string Name(int type, bool ipv6)
    {
        if (ipv6)
        {
            return type switch
            {
                1 => "Destination unreachable",
                2 => "Packet too big",
                3 => "Time exceeded",
                4 => "Parameter problem",
                128 => "Echo request",
                129 => "Echo reply",
                133 => "Router solicitation",
                134 => "Router advertisement",
                135 => "Neighbor solicitation",
                136 => "Neighbor advertisement",
                137 => "Redirect",
                _ => $"Type {type}"};
        }

        return type switch
        {
            0 => "Echo reply",
            3 => "Destination unreachable",
            4 => "Source quench",
            5 => "Redirect",
            8 => "Echo request",
            11 => "Time exceeded",
            12 => "Parameter problem",
            13 => "Timestamp request",
            14 => "Timestamp reply",
            17 => "Address mask request",
            18 => "Address mask reply",
            _ => $"Type {type}"};
    }
}
