using System.Buffers.Binary;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.Packets;

/// <summary>解析キュー投入前の endpoint 照合と fingerprint が共有する境界検証です。</summary>
internal static class IpPacketReader
{
    /// <summary>対応するリンクヘッダーを越え、完全な IP パケットの範囲を返します。</summary>
    /// <returns>IP とトランスポート位置を検証できたか。</returns>
    internal static bool TryRead(ReadOnlySpan<byte> data, int linkType, out IpPacketInfo packet, bool requireComplete = true)
    {
        packet = default;
        int offset;
        int etherType = 0;
        switch (linkType)
        {
            case 1:
                if (data.Length < 14)
                {
                    return false;
                }
                offset = 14;
                etherType = BinaryPrimitives.ReadUInt16BigEndian(data[12..]);
                int tags = 0;
                while (etherType is 0x8100 or 0x88a8 && tags++ < 4)
                {
                    if (data.Length < offset + 4)
                    {
                        return false;
                    }
                    etherType = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]);
                    offset += 4;
                }

                break;
            case 0:
            case 108:
                offset = 4;
                break;
            case 12:
            case 101:
            case 228:
            case 229:
                offset = 0;
                break;
            case 113:
                if (data.Length < 16)
                {
                    return false;
                }
                etherType = BinaryPrimitives.ReadUInt16BigEndian(data[14..]);
                offset = 16;
                break;
            case 276:
                if (data.Length < 20)
                {
                    return false;
                }
                etherType = BinaryPrimitives.ReadUInt16BigEndian(data);
                offset = 20;
                break;
            default:
                return false;
        }

        if (data.Length <= offset || etherType != 0 && etherType is not (0x0800 or 0x86dd))
        {
            return false;
        }
        int version = data[offset] >> 4;
        if (etherType == 0x0800 && version != 4 || etherType == 0x86dd && version != 6)
        {
            return false;
        }
        return version == 4 ? TryReadV4(data, offset, requireComplete, out packet)
                            : version == 6 && TryReadV6(data, offset, requireComplete, out packet);
    }

    /// <summary>IPv4 の長さと断片位置を検査します。</summary>
    /// <returns>完全な IPv4 原本であるか。</returns>
    private static bool TryReadV4(ReadOnlySpan<byte> data, int offset, bool requireComplete, out IpPacketInfo packet)
    {
        packet = default;
        if (data.Length - offset < 20)
        {
            return false;
        }
        int header = (data[offset] & 15) * 4;
        int length = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]);
        if (header < 20 || length < header || data.Length - offset < header || requireComplete && data.Length - offset < length)
        {
            return false;
        }
        length = Math.Min(length, data.Length - offset);
        bool fragment = (BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 6)..]) & 0x1fff) != 0;
        packet = new IpPacketInfo(offset, length, 4, offset + 12, offset + 16, 4, offset + header, data[offset + 9], fragment);
        return true;
    }

    /// <summary>IPv6 の拡張ヘッダーを有界にたどります。</summary>
    /// <returns>完全な IPv6 原本であるか。</returns>
    private static bool TryReadV6(ReadOnlySpan<byte> data, int offset, bool requireComplete, out IpPacketInfo packet)
    {
        packet = default;
        if (data.Length - offset < 40)
        {
            return false;
        }
        int length = 40 + BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 4)..]);
        if (requireComplete && data.Length - offset < length)
        {
            return false;
        }
        length = Math.Min(length, data.Length - offset);
        int end = offset + length;
        int transportOffset = offset + 40;
        byte protocol = data[offset + 6];
        bool fragment = false;
        int headers = 0;
        while (protocol is 0 or 43 or 60 or 44 or 51)
        {
            if (16 < ++headers || end - transportOffset < 8)
            {
                return false;
            }
            int size = protocol switch
            {
                44 => 8,
                51 => (data[transportOffset + 1] + 2) * 4,
                _ => (data[transportOffset + 1] + 1) * 8
            };
            if (size < 8 || end - transportOffset < size)
            {
                return false;
            }
            if (protocol == 44)
            {
                fragment = (BinaryPrimitives.ReadUInt16BigEndian(data[(transportOffset + 2)..]) & 0xfff8) != 0;
            }

            protocol = data[transportOffset];
            transportOffset += size;
            if (fragment)
            {
                break;
            }
        }

        packet = new IpPacketInfo(offset, length, 6, offset + 8, offset + 24, 16, transportOffset, protocol, fragment);
        return true;
    }
}
