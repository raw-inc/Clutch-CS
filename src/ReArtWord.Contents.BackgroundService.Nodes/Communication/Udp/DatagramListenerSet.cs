using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace ReArtWord.Contents.BackgroundService.Nodes.Communication.Udp;

/// <summary>複数UDPポートを一つの有限受信キューと待受寿命へまとめます。</summary>
internal sealed class DatagramListenerSet : IAsyncDisposable
{
    private readonly List<DatagramSocketLease> sockets = [];
    private readonly Channel<UdpReceiveResult> queue = Channel.CreateBounded<UdpReceiveResult>(256);

    /// <summary>全ポートの確保後に通知する待受先です。</summary>
    internal string Endpoints => string.Join(", ", sockets.Select(socket => socket.Endpoint.ToString()));

    /// <summary>呼出側が破棄を保証した集合へ、確保できた購読を順に登録します。</summary>
    internal void Open(string interfaceId, IPAddress address, IReadOnlyList<int> ports, CancellationToken cancellationToken = default)
    {
        foreach (int port in ports)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sockets.Add(DatagramSocketLease.Open(interfaceId, address, port, receiveQueue: queue));
        }
    }

    /// <summary>どのポートから届いた入力も同じ受付へ渡します。</summary>
    /// <returns>一件のUDPパケットです。</returns>
    internal ValueTask<UdpReceiveResult> ReadAsync(CancellationToken cancellationToken = default) => queue.Reader.ReadAsync(cancellationToken);

    /// <summary>このノードの全購読を解除し、最後の共有ソケットの終了を待ちます。</summary>
    /// <returns>所有した購読の解放です。</returns>
    // 抑制理由: IAsyncDisposableの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "IAsyncDisposableの固定署名です。")]
    public async ValueTask DisposeAsync()
    {
        foreach (DatagramSocketLease socket in sockets)
        {
            await socket.DisposeAsync().ConfigureAwait(false);
        }
        sockets.Clear();
    }
}
