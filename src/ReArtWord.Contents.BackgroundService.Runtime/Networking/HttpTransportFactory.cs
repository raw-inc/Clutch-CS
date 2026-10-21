using System.Net;
using System.Net.Sockets;

namespace ReArtWord.Contents.BackgroundService.Runtime.Networking;

/// <summary>明示NICから接続するHTTP transportを共有します。</summary>
public static class HttpTransportFactory
{
    /// <summary>redirectとproxyを無効にしてIPv4 ソケットを指定NICへbindします。</summary>
    /// <returns>呼出側が所有して破棄するhandlerです。</returns>
    public static SocketsHttpHandler Create(string interfaceId, string localAddress, int localPort = 0) => Create(new LocalNetworkBinding(interfaceId, localAddress), localPort);

    /// <summary>実行PCに固定したNICとIPからHTTP接続を開始します。</summary>
    /// <returns>呼出側が所有して破棄するhandlerです。</returns>
    public static SocketsHttpHandler Create(LocalNetworkBinding binding, int localPort = 0)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (localPort is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(localPort));
        }
        _ = binding.Resolve();
        SocketsHttpHandler handler = new()
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
        };
        handler.ConnectCallback = async (connection, token) =>
        {
            Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            bool connected = false;
            try
            {
                socket.Bind(new IPEndPoint(binding.Resolve(), localPort));
                await socket.ConnectAsync(connection.DnsEndPoint, token).ConfigureAwait(false);
                connected = true;
                return new NetworkStream(socket, true);
            }
            finally
            {
                if (!connected)
                {
                    socket.Dispose();
                }
            }
        };
        return handler;
    }
}
