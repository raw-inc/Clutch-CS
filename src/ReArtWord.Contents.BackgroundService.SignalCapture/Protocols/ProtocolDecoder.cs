using System.Buffers.Binary;
using System.Text;
using ReArtWord.Contents.BackgroundService.SignalCapture.Packets;
using ReArtWord.Contents.BackgroundService.SignalCapture.Protocols.Http;
using ReArtWord.Contents.BackgroundService.SignalCapture.Protocols.Osc;
using ReArtWord.Contents.BackgroundService.SignalCapture.Text;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.Protocols;

/// <summary>ポート番号に依存せず OSC と Art-Net を判別します。</summary>
internal static class ProtocolDecoder
{
    private static string oscProtocol => "OSC";
    private static string artNetProtocol => "Art-Net";

    /// <summary>原本の保持量を変えず、閲覧対象の詳細だけを展開します。</summary>
    /// <returns>全チャンネルを含む閲覧用表示。</returns>
    internal static string Describe(PacketRecord packet)
    {
        if (packet.Http is not null)
        {
            string description = packet.HttpMessages.Count == 0 ? HttpDecoder.Describe(packet.Http, true)
                                                                : string.Join("\n---\n", packet.HttpMessages.Select(message => HttpDecoder.Describe(message, true)));
            return description + UiText.Get("\nHTTP is reassembled TCP data. Hex shows the original frame.");
        }

        ReadOnlySpan<byte> data = packet.Data.AsSpan(packet.PayloadOffset, packet.PayloadLength);
        if (string.Equals(packet.Protocol, artNetProtocol, StringComparison.Ordinal))
        {
            try
            {
                return WithCaptureWarning(DecodeArtNet(data, true, true), packet);
            }
            catch (FormatException exception)
            {
                return UiText.Get("Cannot decode: ") + UiText.Diagnostic(exception.Message);
            }
        }

        if (string.Equals(packet.Protocol, oscProtocol, StringComparison.Ordinal))
        {
            try
            {
                return WithCaptureWarning(OscCodec.Decode(data, localize: true), packet);
            }
            catch (FormatException exception)
            {
                return UiText.Get("Cannot decode: ") + UiText.Diagnostic(exception.Message);
            }
        }

        return UiText.Japanese ? string.Join("\n", packet.Details.Split('\n').Select(UiText.Diagnostic))
                               : packet.Details;
    }

    /// <summary>表示を再構築しても取得原本の欠落に関する案内を維持します。</summary>
    /// <returns>必要に応じて欠落の説明を加えた詳細。</returns>
    private static string WithCaptureWarning(string details, PacketRecord packet)
    {
        if (packet.Data.Length < packet.OriginalLength)
        {
            return details + UiText.Get("\nThe captured length is shorter than the original; data is missing.");
        }

        return details;
    }

    /// <summary>UDP の内容を解析して表示へ反映します。</summary>
    internal static void Decode(PacketRecord packet, bool includeChannels = false)
    {
        ReadOnlySpan<byte> data = packet.Data.AsSpan(packet.PayloadOffset, packet.PayloadLength);
        try
        {
            if ((packet.SourcePort is 53 or 5353 || packet.DestinationPort is 53 or 5353) && !OscCodec.HasMessageHeader(data) && !data.StartsWith("Art-Net\0"u8))
            {
                DnsDecoder.Decode(packet);
                return;
            }

            if (data.StartsWith("Art-Net\0"u8))
            {
                packet.Protocol = "Art-Net";
                packet.Details = DecodeArtNet(data, includeChannels);
                packet.Summary = packet.Details.Split('\n')[0].TrimEnd();
                SetArtNetValue(packet, data);
            }
            else
            {
                if (data.StartsWith("/"u8) || data.StartsWith("#bundle\0"u8))
                {
                    packet.Protocol = "OSC";
                    var preview = new OscPreview(data.StartsWith("#bundle\0"u8));
                    packet.Details = OscCodec.Decode(data, preview: preview);
                    packet.Paths = preview.Paths;
                    packet.Value = preview.Value;
                    packet.Summary = packet.Details.Split('\n')[0].TrimEnd();
                }
            }
        }
        catch (FormatException exception)
        {
            packet.Summary = "Cannot decode: " + exception.Message;
            packet.Details = packet.Summary;
            packet.ContentProtocol = "";
            packet.Value = ValuePreview.Hex(data);
        }
    }

    /// <summary>検証済み Art-Net の内包規格と値を一覧へ反映します。</summary>
    private static void SetArtNetValue(PacketRecord packet, ReadOnlySpan<byte> data)
    {
        int opcode = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]);
        if (opcode is 0x5000 or 0x5100)
        {
            packet.ContentProtocol = "DMX512";
            int length = BinaryPrimitives.ReadUInt16BigEndian(data[16..]);
            var value = new ValuePreview();
            for (int i = 0; i < length && !value.IsTruncated; i++)
            {
                value.Append((i == 0 ? ""
                                     : " ") + data[18 + i].ToString(CultureInfo.InvariantCulture));
            }

            packet.Value = value.ToString();
            return;
        }

        if (opcode == 0x8300)
        {
            packet.ContentProtocol = "RDM";
            packet.Value = ValuePreview.Hex(data[24..]);
            return;
        }

        packet.Value = ValuePreview.Hex(data[10..]);
    }

    /// <summary>Art-Net の共通ヘッダーと代表的なパケットを解析します。</summary>
    /// <returns>Universe やチャンネルを含む詳細。</returns>
    private static string DecodeArtNet(ReadOnlySpan<byte> data, bool includeChannels, bool localize = false)
    {
        FrameDecoder.Need(data, 10);
        ushort opcode = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]);
        if (opcode == 0x2100)
        {
            FrameDecoder.Need(data, 108);
            return $"ArtPollReply\n{Layer("none (control message)", localize)}\nIP: {data[10]}.{data[11]}.{data[12]}.{data[13]}\nShort name: {ReadName(data.Slice(26, 18))}\nLong name: {ReadName(data.Slice(44, 64))}";
        }

        FrameDecoder.Need(data, 12);
        ushort version = BinaryPrimitives.ReadUInt16BigEndian(data[10..]);
        if (version < 14)
        {
            throw new FormatException("Art-Net ProtVer < 14");
        }

        if (opcode == 0x2000)
        {
            FrameDecoder.Need(data, 14);
            return $"ArtPoll\n{Layer("none (control message)", localize)}\nProtVer: {version}\nTalkToMe: 0x{data[12]:X2}\nPriority: {data[13]}";
        }

        if (opcode == 0x5200)
        {
            FrameDecoder.Need(data, 14);
            return $"ArtSync\n{Layer("none (control message)", localize)}\nProtVer: {version}";
        }

        if (opcode == 0x8300)
        {
            FrameDecoder.Need(data, 25);
            if (127 < data[21])
            {
                throw new FormatException("ArtRdm Net exceeds 127.");
            }

            return $"ArtRdm U={(data[21] << 8) | data[23]}\n{Layer("RDM", localize)}\nProtVer: {version}\nRdmVer: {data[12]}\nCommand: 0x{data[22]:X2}\nRDM payload: {data.Length - 24} bytes\n{DisplayText.Hex(data[24..])}";
        }

        if (opcode is not (0x5000 or 0x5100))
        {
            string unsupported = "Field decoding for this OpCode is not supported. Inspect the Hex data.";
            return $"Art-Net OpCode: 0x{opcode:X4}\n{Layer("not decoded", localize)}\nProtVer: {version}\n" + (localize ? UiText.Get(unsupported)
                                                                                                                        : unsupported);
        }

        FrameDecoder.Need(data, 18);
        int universe = BinaryPrimitives.ReadUInt16LittleEndian(data[14..]);
        int length = BinaryPrimitives.ReadUInt16BigEndian(data[16..]);
        bool nonZero = opcode == 0x5100;
        if (32767 < universe || length < 1 || 512 < length || !nonZero && (length < 2 || length % 2 != 0) || nonZero && data[13] is 0 or 0xcc)
        {
            throw new FormatException("ArtDmx Universe / Length");
        }

        FrameDecoder.Need(data, 18 + length);
        var result = new StringBuilder();
        string kind = nonZero ? "ArtNzs"
                              : "ArtDmx";
        result.AppendLine(CultureInfo.InvariantCulture, $"{kind}  U={universe}  Seq={data[12]}  {length} ch");
        result.AppendLine(Layer("DMX512", localize));
        result.AppendLine(CultureInfo.InvariantCulture, $"Net: {universe >> 8}  Sub-Net: {(universe >> 4) & 15}  Universe: {universe & 15}");
        result.AppendLine(CultureInfo.InvariantCulture, $"Port-Address (0-based): {universe}\nProtVer: {version}");
        result.AppendLine(nonZero ? $"StartCode: 0x{data[13]:X2}"
                                  : $"StartCode: 0x00\nPhysical: {data[13]}");
        string sequenceInfo = "Sequence 0 = no sequence checking / Channels are 1-based";
        result.AppendLine(localize ? UiText.Get(sequenceInfo)
                                   : sequenceInfo);
        if (!includeChannels)
        {
            return result.ToString();
        }

        for (int i = 0; i < length; i += 8)
        {
            result.Append(CultureInfo.InvariantCulture, $"CH {i + 1, 3}: ");
            for (int j = i; j < Math.Min(i + 8, length); j++)
            {
                result.Append(CultureInfo.InvariantCulture, $"{data[18 + j], 3} ");
            }

            result.AppendLine();
        }

        return result.ToString();
    }

    /// <summary>制御パケットを DMX データと誤表示しない共通ラベルです。</summary>
    /// <returns>内包プロトコルの表示行。</returns>
    private static string Layer(string protocol, bool localize)
    {
        if (localize)
        {
            return UiText.Get("Encapsulated protocol: ") + UiText.Get(protocol);
        }

        return "Encapsulated protocol: " + protocol;
    }

    /// <summary>終端付きの名称を読み取ります。</summary>
    /// <returns>制御文字を除去した名称。</returns>
    private static string ReadName(ReadOnlySpan<byte> data)
    {
        int end = data.IndexOf((byte)0);
        return DisplayText.Safe(Encoding.UTF8.GetString(end < 0 ? data
                                                                : data[..end]));
    }
}
