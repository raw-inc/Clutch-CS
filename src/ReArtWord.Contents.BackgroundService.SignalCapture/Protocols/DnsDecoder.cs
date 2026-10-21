using System.Buffers.Binary;
using System.Net;
using System.Text;
using ReArtWord.Contents.BackgroundService.SignalCapture.Packets;
using ReArtWord.Contents.BackgroundService.SignalCapture.Text;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.Protocols;

/// <summary>UDP DNS/mDNS の名前圧縮と代表的なリソースレコードを解析します。</summary>
internal static class DnsDecoder
{
    /// <summary>名前と値の表示量を制限しつつ、宣言された全レコードの境界を検証します。</summary>
    internal static void Decode(PacketRecord packet)
    {
        bool multicast = packet.SourcePort == 5353 || packet.DestinationPort == 5353;
        packet.Protocol = multicast ? "mDNS"
                                    : "DNS";
        packet.ContentProtocol = "DNS";
        ReadOnlySpan<byte> data = packet.Data.AsSpan(packet.PayloadOffset, packet.PayloadLength);
        FrameDecoder.Need(data, 12);
        int id = BinaryPrimitives.ReadUInt16BigEndian(data);
        int flags = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        int questions = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
        int answers = BinaryPrimitives.ReadUInt16BigEndian(data[6..]);
        int authorities = BinaryPrimitives.ReadUInt16BigEndian(data[8..]);
        int additional = BinaryPrimitives.ReadUInt16BigEndian(data[10..]);
        string action = (flags & 0x8000) == 0 ? "Query"
                                              : "Response";
        var details = new StringBuilder();
        details.AppendLine(CultureInfo.InvariantCulture, $"{packet.Protocol} {action}\nID: 0x{id:X4}\nFlags: 0x{flags:X4} / RCODE: {flags & 15}\nQuestions: {questions} / Answers: {answers} / Authority: {authorities} / Additional: {additional}");
        var preview = new ValuePreview();
        int offset = 12;
        int nameBudget = 4096;
        for (int i = 0; i < questions; i++)
        {
            string name = ReadName(data, ref offset, data.Length, ref nameBudget);
            FrameDecoder.Need(data, offset + 4);
            int type = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            int queryClass = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]);
            bool unicastReply = multicast && (queryClass & 0x8000) != 0;
            queryClass = multicast ? queryClass & 0x7fff
                                   : queryClass;
            offset += 4;
            string question = $"{TypeName(type)} {name}";
            if (i < 16)
            {
                details.AppendLine(CultureInfo.InvariantCulture, $"Question: {question} / Class: {queryClass} / QU: {unicastReply}");
            }

            if (i != 0)
            {
                preview.Append("; ");
            }

            preview.Append(question);
        }

        int records = answers + authorities + additional;
        for (int i = 0; i < records; i++)
        {
            string name = ReadName(data, ref offset, data.Length, ref nameBudget);
            FrameDecoder.Need(data, offset + 10);
            int type = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            int recordClass = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]);
            bool cacheFlush = multicast && (recordClass & 0x8000) != 0;
            recordClass = multicast ? recordClass & 0x7fff
                                    : recordClass;
            uint ttl = BinaryPrimitives.ReadUInt32BigEndian(data[(offset + 4)..]);
            int length = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 8)..]);
            offset += 10;
            FrameDecoder.Need(data, offset + length);
            string value = ReadRecord(data, type, offset, length, ref nameBudget);
            offset += length;
            if (i < 16)
            {
                details.AppendLine(CultureInfo.InvariantCulture, $"Record: {name} {TypeName(type)} {value} / TTL: {ttl} / Class: {recordClass} / Cache flush: {cacheFlush}");
            }

            if (questions != 0 || i != 0)
            {
                preview.Append("; ");
            }

            preview.Append(TypeName(type) + "=" + value);
        }

        if (offset != data.Length)
        {
            throw new FormatException("DNS message has trailing data.");
        }

        if (16 < questions || 16 < records)
        {
            details.AppendLine("...");
        }

        packet.Summary = $"{action} ID=0x{id:X4} Q={questions} AN={answers} RCODE={flags & 15}";
        packet.Details = details.ToString();
        packet.Value = preview.ToString();
    }

    /// <summary>参照循環・領域外参照・長すぎる展開名を拒否します。</summary>
    /// <returns>表示用の名前。実パケットの名前は変更しません。</returns>
    private static string ReadName(ReadOnlySpan<byte> data, ref int offset, int end, ref int budget)
    {
        int cursor = offset;
        int next = -1;
        int expanded = 1;
        var name = new StringBuilder();
        for (int step = 0; step < 256; step++)
        {
            if (budget-- <= 0)
            {
                throw new FormatException("DNS name processing limit exceeded.");
            }

            FrameDecoder.Need(data, cursor + 1);
            if (next < 0 && end <= cursor)
            {
                throw new FormatException("DNS name exceeds its record.");
            }

            int length = data[cursor++];
            if ((length & 0xc0) == 0xc0)
            {
                FrameDecoder.Need(data, cursor + 1);
                if (next < 0 && end <= cursor)
                {
                    throw new FormatException("DNS name pointer is truncated.");
                }

                int target = ((length & 63) << 8) | data[cursor++];
                if (target < 12 || cursor - 2 <= target)
                {
                    throw new FormatException("DNS compression pointer must refer to a previous name.");
                }

                if (next < 0)
                {
                    next = cursor;
                }

                cursor = target;
                continue;
            }

            if (63 < length)
            {
                throw new FormatException("Unsupported DNS label encoding.");
            }

            if (length == 0)
            {
                offset = next < 0 ? cursor
                                  : next;
                return name.Length == 0 ? "."
                                        : name.ToString();
            }

            FrameDecoder.Need(data, cursor + length);
            expanded += length + 1;
            if (255 < expanded || next < 0 && end < cursor + length)
            {
                throw new FormatException("DNS name is too long or exceeds its record.");
            }

            if (name.Length != 0)
            {
                name.Append('.');
            }

            ReadOnlySpan<byte> label = data.Slice(cursor, length);
            name.Append(System.Text.Unicode.Utf8.IsValid(label) ? DisplayText.Safe(Encoding.UTF8.GetString(label)).Replace(".", "\\.", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal)
                                                                : "0x" + Convert.ToHexString(label));
            cursor += length;
        }

        throw new FormatException("DNS name compression loop or excessive indirection.");
    }

    /// <summary>RDATA の長さを守って代表的なレコード値を表示します。</summary>
    /// <returns>レコード値。</returns>
    private static string ReadRecord(ReadOnlySpan<byte> data, int type, int offset, int length, ref int budget)
    {
        int end = offset + length;
        if (type is 1 or 28)
        {
            int expected = type == 1 ? 4
                                     : 16;
            if (length != expected)
            {
                throw new FormatException("DNS address record length is invalid.");
            }

            return new IPAddress(data.Slice(offset, length)).ToString();
        }

        if (type is 2 or 5 or 12 or 15 or 33 or 6)
        {
            string prefix = "";
            int fixedLength = type switch
            {
                15 => 2,
                33 => 6,
                _ => 0
            };
            if (length < fixedLength + 1)
            {
                throw new FormatException("DNS name record is truncated.");
            }

            if (type == 15)
            {
                prefix = $"{BinaryPrimitives.ReadUInt16BigEndian(data[offset..])} ";
            }

            if (type == 33)
            {
                prefix = $"Priority={BinaryPrimitives.ReadUInt16BigEndian(data[offset..])} Weight={BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..])} Port={BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 4)..])} ";
            }

            offset += fixedLength;
            string name = ReadName(data, ref offset, end, ref budget);
            if (type == 6)
            {
                string mailbox = ReadName(data, ref offset, end, ref budget);
                if (end - offset != 20)
                {
                    throw new FormatException("DNS SOA record length is invalid.");
                }

                name += $" {mailbox} Serial={BinaryPrimitives.ReadUInt32BigEndian(data[offset..])}";
                offset += 20;
            }

            if (offset != end)
            {
                throw new FormatException("DNS name record has trailing data.");
            }

            return prefix + name;
        }

        if (type == 16)
        {
            var text = new ValuePreview();
            while (offset < end)
            {
                int size = data[offset++];
                if (end - offset < size)
                {
                    throw new FormatException("DNS TXT string exceeds its record.");
                }

                text.Append("\"" + DisplayText.Safe(Encoding.UTF8.GetString(data.Slice(offset, size))) + "\" ");
                offset += size;
            }

            return text.ToString();
        }

        return ValuePreview.Hex(data.Slice(offset, length));
    }

    /// <summary>レコードの型名を返します。</summary>
    /// <returns>既知の型名または数値。</returns>
    private static string TypeName(int type) => type switch
    {
        1 => "A",
        2 => "NS",
        5 => "CNAME",
        6 => "SOA",
        12 => "PTR",
        15 => "MX",
        16 => "TXT",
        28 => "AAAA",
        33 => "SRV",
        41 => "OPT",
        255 => "ANY",
        _ => $"TYPE{type}"};
}
