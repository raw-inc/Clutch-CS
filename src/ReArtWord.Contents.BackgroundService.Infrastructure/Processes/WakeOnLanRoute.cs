using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Network;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Processes;

/// <summary>保存済みのIPv4リンクから、WoL送信元の候補を比較します。</summary>
public static class WakeOnLanRoute
{
    /// <summary>DNS・ARPや新しい通信を伴わず、保存されたネットワークだけを照合します。</summary>
    /// <returns>候補が有効なIPv4プレフィックス内にあるかです。</returns>
    public static bool Contains(string address, DeviceIpAddress network)
    {
        ArgumentNullException.ThrowIfNull(network);
        if (network.PrefixLength is < 1 or > 30 || !IPAddress.TryParse(address, out IPAddress? candidate) || !IPAddress.TryParse(network.Address, out IPAddress? saved)
            || candidate.AddressFamily != AddressFamily.InterNetwork || saved.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }
        uint mask = uint.MaxValue << (32 - network.PrefixLength);
        return (BinaryPrimitives.ReadUInt32BigEndian(candidate.GetAddressBytes()) & mask) == (BinaryPrimitives.ReadUInt32BigEndian(saved.GetAddressBytes()) & mask);
    }
}
