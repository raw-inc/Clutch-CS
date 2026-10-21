using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;

namespace ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;

/// <summary>CoreのNICではなくクライアント自身のOSから明示選択候補を取得します。</summary>
public static class ObserverNetwork
{
    /// <summary>現存するIPv4とブロードキャスト候補を返します。</summary>
    /// <returns>クライアント上のNIC一覧です。</returns>
    public static IReadOnlyList<ClientNetworkInterface> GetInterfaces()
    {
        return NetworkInterface.GetAllNetworkInterfaces().Select(Read).ToArray();
    }

    /// <summary>一NICの照会不能を他のNICへ暗黙置換しません。</summary>
    /// <returns>一つのNICの現在情報です。</returns>
    private static ClientNetworkInterface Read(NetworkInterface network)
    {
        try
        {
            ClientNetworkAddress[] addresses = network.GetIPProperties().UnicastAddresses.Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(address => new ClientNetworkAddress(address.Address.ToString(), address.PrefixLength, Broadcast(address))).ToArray();
            return new(network.Id, network.Name, network.OperationalStatus == OperationalStatus.Up, addresses);
        }
        catch (NetworkInformationException)
        {
            return new(network.Id, network.Name, false, []);
        }
    }

    /// <summary>実割当のmaskから候補範囲を計算します。</summary>
    /// <returns>IPv4 ブロードキャスト候補です。</returns>
    private static string Broadcast(UnicastIPAddressInformation information)
    {
        byte[] address = information.Address.GetAddressBytes();
        byte[] mask = information.IPv4Mask.GetAddressBytes();
        for (int index = 0; index < address.Length; index++)
        {
            address[index] |= (byte)~mask[index];
        }
        return new IPAddress(address).ToString();
    }
}
