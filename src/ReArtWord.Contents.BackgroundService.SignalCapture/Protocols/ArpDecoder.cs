using System.Buffers.Binary;
using System.Net;
using ReArtWord.Contents.BackgroundService.SignalCapture.Packets;
using ReArtWord.Contents.BackgroundService.SignalCapture.Text;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.Protocols;

/// <summary>ARP の可変長アドレス領域と要求・応答を解析します。</summary>
internal static class ArpDecoder
{
    /// <summary>Ethernet ヘッダーの宛先と ARP の問い合わせ先を区別します。</summary>
    internal static void Decode(PacketRecord packet, int offset)
    {
        packet.Protocol = "ARP";
        ReadOnlySpan<byte> data = packet.Data.AsSpan(offset);
        FrameDecoder.Need(data, 8);
        int hardware = BinaryPrimitives.ReadUInt16BigEndian(data);
        int protocol = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        int hardwareLength = data[4];
        int protocolLength = data[5];
        int operation = BinaryPrimitives.ReadUInt16BigEndian(data[6..]);
        if (hardwareLength == 0 || protocolLength == 0)
        {
            throw new FormatException("ARP address length is zero.");
        }

        int length = 8 + 2 * (hardwareLength + protocolLength);
        FrameDecoder.Need(data, length);
        string senderHardware = Convert.ToHexString(data.Slice(8, hardwareLength));
        string targetHardware = Convert.ToHexString(data.Slice(8 + hardwareLength + protocolLength, hardwareLength));
        ReadOnlySpan<byte> senderAddress = data.Slice(8 + hardwareLength, protocolLength);
        ReadOnlySpan<byte> targetAddress = data.Slice(8 + 2 * hardwareLength + protocolLength, protocolLength);
        bool ipv4 = protocol == 0x0800 && protocolLength == 4;
        string sender = ipv4 ? new IPAddress(senderAddress).ToString()
                             : Convert.ToHexString(senderAddress);
        string target = ipv4 ? new IPAddress(targetAddress).ToString()
                             : Convert.ToHexString(targetAddress);
        string action = operation switch
        {
            1 => "Request",
            2 => "Reply",
            3 => "Reverse request",
            4 => "Reverse reply",
            _ => $"Operation {operation}"};
        if (ipv4)
        {
            packet.Source = sender;
            packet.Destination = target;
        }

        packet.PayloadOffset = offset;
        packet.PayloadLength = length;
        packet.Summary = $"ARP {action}: {sender} > {target}";
        packet.Details = $"ARP {action}\nHardware type: {hardware} / address length: {hardwareLength}\nProtocol type: 0x{protocol:X4} / address length: {protocolLength}\nSender address: {sender}\nSender hardware: {senderHardware}\nTarget address: {target}\nTarget hardware: {targetHardware}";
        var value = new ValuePreview();
        value.Append($"{sender} > {target} / {senderHardware}");
        packet.Value = value.ToString();
    }
}
