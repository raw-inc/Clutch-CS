using System.Buffers.Binary;
using System.Net;
using ReArtWord.Contents.BackgroundService.SignalCapture.Protocol;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.Packets;

/// <summary>中継用 TCP / UDP の再キャプチャを、デコーダーとキューより前で抑止します。</summary>
internal sealed class EndpointFilter
{
    private readonly List<(byte Protocol, byte[] Address, int Port)> endpoints = new();

    /// <summary>IP リテラルだけを受け付け、DNS 問合せを行いません。</summary>
    internal EndpointFilter(EndpointRequest[]? requests, TransportEndpointRequest[]? transportRequests = null)
    {
        if (256 < (requests?.Length ?? 0) + (transportRequests?.Length ?? 0))
        {
            throw new ArgumentException("Too many excluded endpoints.");
        }
        foreach (EndpointRequest request in requests ?? Array.Empty<EndpointRequest>())
        {
            ArgumentNullException.ThrowIfNull(request);
            Add(6, request.Address, request.Port);
        }
        foreach (TransportEndpointRequest request in transportRequests ?? Array.Empty<TransportEndpointRequest>())
        {
            ArgumentNullException.ThrowIfNull(request);
            byte protocol = request.Transport switch
            {
                "tcp" => 6,
                "udp" => 17,
                _ => throw new ArgumentException("Invalid excluded endpoint transport.")
            };
            Add(protocol, request.Address, request.Port);
        }
    }

    /// <summary>不特定アドレスへの除外拡大を防ぎ、明示した IP を正規化します。</summary>
    private void Add(byte protocol, string addressText, int port)
    {
        if (!IPAddress.TryParse(addressText, out IPAddress? address) || port is < 0 or > 65535)
        {
            throw new ArgumentException("Invalid excluded endpoint.");
        }
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            throw new ArgumentException("An explicit endpoint address is required.");
        }
        endpoints.Add((protocol, address.GetAddressBytes(), port));
    }

    /// <summary>両方向を照合し、ポートがない後続断片は同じ transport / IP を保守的に除外します。</summary>
    /// <returns>再取得を抑止すべき原本であるか。</returns>
    internal bool Excludes(ReadOnlySpan<byte> data, int linkType)
    {
        if (endpoints.Count == 0 || !IpPacketReader.TryRead(data, linkType, out IpPacketInfo ip, false) || ip.Transport is not (6 or 17))
        {
            return false;
        }
        int remaining = ip.Offset + ip.Length - ip.TransportOffset;
        foreach ((byte protocol, byte[] address, int port) in endpoints)
        {
            if (ip.Transport != protocol)
            {
                continue;
            }
            bool source = data.Slice(ip.SourceOffset, ip.AddressLength).SequenceEqual(address);
            bool destination = data.Slice(ip.DestinationOffset, ip.AddressLength).SequenceEqual(address);
            if (!source && !destination)
            {
                continue;
            }
            if (ip.NonInitialFragment || remaining < 4)
            {
                return true;
            }
            if (source && BinaryPrimitives.ReadUInt16BigEndian(data[ip.TransportOffset..]) == port ||
                destination && BinaryPrimitives.ReadUInt16BigEndian(data[(ip.TransportOffset + 2)..]) == port)
            {
                return true;
            }
        }

        return false;
    }
}
