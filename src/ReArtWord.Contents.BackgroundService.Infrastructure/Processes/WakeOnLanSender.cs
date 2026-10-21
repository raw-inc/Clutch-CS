using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Processes;

/// <summary>確認されたMACだけを、ローカルIPv4リンクからMagic Packetとして送信します。</summary>
public sealed class WakeOnLanSender
{
    private readonly Func<IReadOnlyList<(IPAddress Address, IPAddress Broadcast, int Index)>> interfaces;
    private readonly int port;
    private static SocketOptionName WindowsUnicastInterface => (SocketOptionName)31;

    /// <summary>このPCで有効なIPv4リンクと標準のUDP宛先9を使います。</summary>
    public WakeOnLanSender() : this(LocalInterfaces, 9)
    {
    }

    /// <summary>実UDP試験を隔離したアドレスとポートへ限定します。</summary>
    internal WakeOnLanSender(Func<IReadOnlyList<(IPAddress Address, IPAddress Broadcast, int Index)>> interfaces, int port)
    {
        this.interfaces = interfaces;
        this.port = port;
    }

    /// <summary>48bitの個別MACだけを許可し、任意のUDP本文を受け付けません。</summary>
    /// <returns>FFを6バイト、MACを16回繰り返した102バイトです。</returns>
    public static byte[] Packet(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        if (32 < address.Length || !PhysicalAddress.TryParse(address, out PhysicalAddress? parsed))
        {
            throw new ArgumentException("wake.invalidMac", nameof(address));
        }
        byte[] mac = parsed.GetAddressBytes();
        if (mac.Length != 6 || (mac[0] & 1) != 0 || !mac.AsSpan().ContainsAnyExcept((byte)0))
        {
            throw new ArgumentException("wake.invalidMac", nameof(address));
        }
        byte[] packet = new byte[102];
        packet.AsSpan(0, 6).Fill(255);
        for (int offset = 6; offset < packet.Length; offset += mac.Length)
        {
            mac.CopyTo(packet, offset);
        }
        return packet;
    }

    /// <summary>送信済みと起動済みを分け、部分失敗も数付きで返します。</summary>
    /// <returns>今回一度のソケット送信結果です。</returns>
    public async Task<HostWakeResult> SendAsync(HostWakeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.HostId);
        if (request.OperationId == Guid.Empty || 120 < request.HostId.Length || request.MacAddresses is null || request.MacAddresses.Count is < 1 or > 128)
        {
            throw new ArgumentException("wake.invalidRequest", nameof(request));
        }
        byte[][] packets = request.MacAddresses.Distinct(StringComparer.OrdinalIgnoreCase).Select(Packet).ToArray();
        if (cancellationToken.IsCancellationRequested)
        {
            return new(request.OperationId, EOutcome.NotExecuted, 0, 0, "wake.cancelled");
        }
        int sent = 0, used = 0, failures = 0;
        bool unconfirmed = false;
        foreach ((IPAddress address, IPAddress broadcast, int index) in interfaces().Distinct().Take(128))
        {
            try
            {
                using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { EnableBroadcast = true };
                socket.Bind(new IPEndPoint(address, 0));
                if (OperatingSystem.IsWindows() && 0 < index)
                {
                    // 複数NICに同じ範囲がある場合も、送信元を選んだインターフェースから出します。
                    socket.SetSocketOption(SocketOptionLevel.IP, WindowsUnicastInterface, IPAddress.HostToNetworkOrder(index));
                }
                bool wrote = false;
                foreach (byte[] packet in packets)
                {
                    await socket.SendToAsync(packet, SocketFlags.None, new IPEndPoint(broadcast, port), cancellationToken).ConfigureAwait(false);
                    sent++;
                    if (!wrote)
                    {
                        used++;
                    }
                    wrote = true;
                }
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException)
            {
                failures++;
                unconfirmed |= exception is OperationCanceledException;
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        if (unconfirmed)
        {
            return new(request.OperationId, EOutcome.Unknown, sent, used, "wake.unconfirmed");
        }
        if (sent == 0)
        {
            return new(request.OperationId, EOutcome.NotExecuted, 0, 0, "wake.noRoute");
        }
        if (failures != 0)
        {
            return new(request.OperationId, EOutcome.Failed, sent, used, "wake.partial");
        }
        return new(request.OperationId, EOutcome.Succeeded, sent, used, "wake.sent");
    }

    /// <summary>仮想スイッチの管理用NICも含め、ローカルの各IPv4リンクへ一度ずつ送ります。</summary>
    /// <returns>実際にbindする送信元と、そのリンク内のbroadcastです。</returns>
    private static IReadOnlyList<(IPAddress Address, IPAddress Broadcast, int Index)> LocalInterfaces()
    {
        List<(IPAddress Address, IPAddress Broadcast, int Index)> result = [];
        foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces().Where(item => item.OperationalStatus == OperationalStatus.Up && item.NetworkInterfaceType != NetworkInterfaceType.Loopback))
        {
            foreach (UnicastIPAddressInformation item in adapter.GetIPProperties().UnicastAddresses)
            {
                if (item.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(item.Address) || item.PrefixLength is < 1 or > 30)
                {
                    continue;
                }
                byte[] address = item.Address.GetAddressBytes(), mask = item.IPv4Mask.GetAddressBytes();
                byte[] broadcast = new byte[4];
                for (int index = 0; index < 4; index++)
                {
                    broadcast[index] = (byte)(address[index] | ~mask[index]);
                }
                result.Add((item.Address, new(broadcast), adapter.GetIPProperties().GetIPv4Properties()?.Index ?? 0));
            }
        }
        return result;
    }
}
