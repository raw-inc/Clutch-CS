using System.Buffers.Binary;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Desktop;

/// <summary>ローカルencoderの共通RTP header、padding、extensionを検証します。</summary>
internal static class DesktopRtpPacket
{
    /// <summary>payload type 96の範囲を割当てずに切り出します。</summary>
    /// <returns>完全なheaderと空でないpayloadがあるか。</returns>
    internal static bool TryPayload(ReadOnlySpan<byte> packet, out ReadOnlySpan<byte> payload)
    {
        payload = default;
        if (packet.Length < 12 || (packet[0] & 0xc0) != 0x80 || (packet[1] & 0x7f) != 96)
        {
            return false;
        }
        int offset = 12 + 4 * (packet[0] & 15), end = packet.Length;
        if ((packet[0] & 0x20) != 0)
        {
            int padding = packet[^1];
            if (padding == 0 || end - offset < padding)
            {
                return false;
            }
            end -= padding;
        }
        if ((packet[0] & 0x10) != 0)
        {
            if (end < offset + 4)
            {
                return false;
            }
            offset += 4 + 4 * BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 2)..]);
        }
        if (end <= offset)
        {
            return false;
        }
        payload = packet[offset..end];
        return true;
    }
}
