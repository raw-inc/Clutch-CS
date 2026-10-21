using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.Runtime.Networking;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Communication;

/// <summary>同じOS観測から得たNICと固定IPv4の対応を検証します。</summary>
public sealed class NetworkBindingTests
{
    /// <summary>UDP受信側のインターフェース索引も、検証したループバックのものです。</summary>
    [Fact]
    public void ResolvedInterfaceMatchesTheFixedAddress()
    {
        NetworkInterface loopback = NetworkInterface.GetAllNetworkInterfaces().Single(network => network.GetIPProperties().UnicastAddresses.Any(item => item.Address.Equals(IPAddress.Loopback)));
        LocalNetworkBinding binding = new(loopback.Id, IPAddress.Loopback.ToString());
        NetworkInterface resolved = binding.ResolveInterface();
        Assert.Equal(loopback.Id, resolved.Id);
        Assert.Equal(loopback.GetIPProperties().GetIPv4Properties().Index, resolved.GetIPProperties().GetIPv4Properties().Index);
        Assert.Equal(IPAddress.Loopback, binding.Resolve());
        Assert.Throws<SocketException>(() => new LocalNetworkBinding(loopback.Id, "192.0.2.254").ResolveInterface());
        Assert.Throws<SocketException>(() => new LocalNetworkBinding(Guid.NewGuid().ToString(), IPAddress.Loopback.ToString()).ResolveInterface());
    }
}
