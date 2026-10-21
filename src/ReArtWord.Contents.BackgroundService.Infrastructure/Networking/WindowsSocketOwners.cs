using System.Buffers.Binary;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Networking;

/// <summary>ローカルTCP・UDPの所有PIDを読み、プロキシの転送経路を特定します。</summary>
[SupportedOSPlatform("windows")]
public static partial class WindowsSocketOwners
{
    /// <summary>シェルを起動せず、現在のソケット表を上限付きで取得します。</summary>
    /// <returns>トランスポート、ローカル・リモート端点と所有PIDです。</returns>
    public static IReadOnlyList<(SocketType Type, IPEndPoint Local, IPEndPoint? Remote, int ProcessId)> Read()
    {
        List<(SocketType, IPEndPoint, IPEndPoint?, int)> result = [];
        ReadTable(result, ipv6: false, udp: false);
        ReadTable(result, ipv6: true, udp: false);
        ReadTable(result, ipv6: false, udp: true);
        ReadTable(result, ipv6: true, udp: true);
        return result;
    }

    /// <summary>一つの表を管理下へコピーし、ネイティブ領域は直ちに解放します。</summary>
    private static void ReadTable(List<(SocketType, IPEndPoint, IPEndPoint?, int)> result, bool ipv6, bool udp)
    {
        uint family = ipv6 ? 23U
                           : 2U;
        uint size = 0;
        uint status = Query(0, ref size, family, udp);
        if (status is not (0 or 122))
        {
            throw new Win32Exception((int)status);
        }
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (size is < 4 or > 8 * 1024 * 1024)
            {
                throw new InvalidDataException("Socket table exceeds its limit.");
            }
            int capacity = checked((int)size);
            nint buffer = Marshal.AllocHGlobal(capacity);
            try
            {
                status = Query(buffer, ref size, family, udp);
                if (status == 122)
                {
                    continue;
                }
                if (status != 0)
                {
                    throw new Win32Exception((int)status);
                }
                int width = (ipv6, udp) switch
                {
                    (true, true) => 28,
                    (true, false) => 56,
                    (false, true) => 12,
                    _ => 24,
                };
                int count = Marshal.ReadInt32(buffer);
                if (capacity < size || count < 0 || (size - 4) / width < count)
                {
                    throw new InvalidDataException("Invalid socket table bounds.");
                }
                byte[] row = new byte[width];
                for (int index = 0; index < count; index++)
                {
                    Marshal.Copy(buffer + 4 + index * width, row, 0, width);
                    int addressOffset = !ipv6 && !udp ? 4
                                                      : 0;
                    int addressLength = ipv6 ? 16
                                             : 4;
                    int portOffset = ipv6 ? 20
                                          : addressOffset + 4;
                    IPEndPoint local = new(new IPAddress(row.AsSpan(addressOffset, addressLength)), BinaryPrimitives.ReadUInt16BigEndian(row.AsSpan(portOffset)));
                    IPEndPoint? remote = null;
                    if (!udp)
                    {
                        int remoteOffset = ipv6 ? 24
                                                : 12;
                        int remotePort = ipv6 ? 44
                                              : 16;
                        remote = new(new IPAddress(row.AsSpan(remoteOffset, addressLength)), BinaryPrimitives.ReadUInt16BigEndian(row.AsSpan(remotePort)));
                    }
                    SocketType kind = udp ? SocketType.Dgram
                                          : SocketType.Stream;
                    result.Add((kind, local, remote, BinaryPrimitives.ReadInt32LittleEndian(row.AsSpan(width - 4))));
                }
                return;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        throw new IOException("Socket table changed repeatedly during inspection.");
    }

    /// <summary>TCPとUDPの公開ABIを同じ所有バッファで呼びます。</summary>
    /// <returns>Win32の結果です。</returns>
    private static uint Query(nint buffer, ref uint size, uint family, bool udp)
        => udp ? GetExtendedUdpTable(buffer, ref size, 0, family, 1, 0)
               : GetExtendedTcpTable(buffer, ref size, 0, family, 5, 0);

    /// <summary>TCPの端点と所有PIDを列挙します。</summary>
    /// <returns>Win32の結果です。</returns>
    [LibraryImport("iphlpapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetExtendedTcpTable(nint buffer, ref uint size, int order, uint family, int kind, uint reserved);

    /// <summary>UDPのbind端点と所有PIDを列挙します。</summary>
    /// <returns>Win32の結果です。</returns>
    [LibraryImport("iphlpapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetExtendedUdpTable(nint buffer, ref uint size, int order, uint family, int kind, uint reserved);
}
