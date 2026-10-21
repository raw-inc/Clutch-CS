using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Runtime.Networking;

/// <summary>指定したNICの同一性とIPv4を通信開始時に検証します。</summary>
public sealed class LocalNetworkBinding
{
    private readonly string interfaceId;
    private readonly IPAddress address;

    /// <summary>実行PCに対応する明示設定を選択します。</summary>
    public LocalNetworkBinding(JsonElement settings, string? hostId) : this(NetworkBindingSettings.Select(settings, hostId))
    {
    }

    /// <summary>選択済みの割り当てを従来と同じNIC・IP検証へ渡します。</summary>
    private LocalNetworkBinding(HostNetworkBinding binding) : this(binding.InterfaceId, binding.LocalAddress)
    {
    }

    /// <summary>共有ソケットの識別に使う、選択済みNIC識別子です。</summary>
    public string InterfaceId => interfaceId;

    /// <summary>明示したインターフェースとIPを固定します。</summary>
    public LocalNetworkBinding(string interfaceId, string localAddress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interfaceId);
        this.interfaceId = interfaceId;
        address = IPAddress.Parse(localAddress);
        if (address.AddressFamily != AddressFamily.InterNetwork || address.Equals(IPAddress.Any))
        {
            throw new FormatException("明示IPv4 localAddressが必要です。");
        }
    }

    /// <summary>同じNICとIPが現在使用可能か検証します。</summary>
    /// <returns>固定したlocal IPです。</returns>
    public IPAddress Resolve() => Resolve(false);

    /// <summary>監視中の同じNIC観測を再利用し、構成変更で失効した観測は採用しません。</summary>
    /// <returns>選択した検証方法で確認した固定local IPです。</returns>
    public IPAddress Resolve(bool useObservation)
    {
        _ = ResolveInterface(useObservation);
        return address;
    }

    /// <summary>検証済みNICの索引などを同じOS観測から取得します。</summary>
    /// <returns>固定したNICとIPv4が使用可能なインターフェースです。</returns>
    public NetworkInterface ResolveInterface(bool useObservation = false)
    {
        try
        {
            IReadOnlyList<NetworkInterface> interfaces;
            if (useObservation)
            {
                interfaces = NetworkInterfaceObservation.Read();
            }
            else
            {
                interfaces = NetworkInterface.GetAllNetworkInterfaces();
            }
            NetworkInterface? network = interfaces.FirstOrDefault(item => string.Equals(item.Id, interfaceId, StringComparison.Ordinal));
            if (network is null || !network.GetIPProperties().UnicastAddresses.Any(item => item.Address.Equals(address)))
            {
                throw new SocketException((int)SocketError.AddressNotAvailable);
            }
            if (network.OperationalStatus != OperationalStatus.Up)
            {
                throw new SocketException((int)SocketError.NetworkDown);
            }
            return network;
        }
        catch (NetworkInformationException)
        {
            // OS照会の失敗も接続実装の一時利用不能へ揃えます。
        }
        throw new SocketException((int)SocketError.NetworkDown);
    }
}
