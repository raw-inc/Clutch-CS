using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace ReArtWord.Contents.BackgroundService.Nodes.Communication.Udp;

/// <summary>同じプロセス内のUDPソケットを共有し、送受信の寿命を購読ごとに管理します。</summary>
internal sealed class DatagramSocketLease : IAsyncDisposable
{
    // IP_UNICAST_IFはWinsockのIPv4オプション31です。.NETの列挙には名前がないためWindowsでだけ設定します。
    private static SocketOptionName UnicastInterface => (SocketOptionName)31;
    private static readonly Lock gate = new();
    private static readonly Dictionary<(string Interface, IPEndPoint Endpoint), Listener> listeners = [];
    private readonly Listener listener;
    private readonly Channel<UdpReceiveResult>? channel;
    private readonly int? interfaceIndex;
    private bool disposed;

    /// <summary>一つのソケットへ独立した受信キューを登録します。</summary>
    private DatagramSocketLease(Listener listener, bool receive, int? interfaceIndex, Channel<UdpReceiveResult>? receiveQueue)
    {
        this.listener = listener;
        this.interfaceIndex = interfaceIndex;
        listener.Owners++;
        if (receive)
        {
            channel = receiveQueue ?? Channel.CreateBounded<UdpReceiveResult>(256);
            listener.Subscribers.Add(channel);
        }
        listener.Start(receive);
    }

    /// <summary>実際に確保した待受先です。ポート0は他の待受と共有しません。</summary>
    internal IPEndPoint Endpoint => listener.Endpoint;

    /// <summary>同じNIC・IP・ポートの既存ソケットを使用します。</summary>
    /// <returns>呼び出し元が破棄する購読です。</returns>
    internal static DatagramSocketLease Open(string interfaceId, IPAddress address, int port, bool receive = true, int? interfaceIndex = null, Channel<UdpReceiveResult>? receiveQueue = null)
    {
        using (gate.EnterScope())
        {
            (string, IPEndPoint) key = (interfaceId, new(address, port));
            if (port == 0 || !listeners.TryGetValue(key, out Listener? listener))
            {
                listener = new Listener(interfaceId, new IPEndPoint(address, port));
                listeners.Add((interfaceId, listener.Endpoint), listener);
                return new(listener, receive, interfaceIndex, receiveQueue);
            }
            return new(listener, receive, interfaceIndex, receiveQueue);
        }
    }

    /// <summary>他のノードの処理速度に依存せず、一件を受け取ります。</summary>
    /// <returns>受信したUDPパケットです。</returns>
    internal ValueTask<UdpReceiveResult> ReadAsync(CancellationToken cancellationToken = default)
        => (channel ?? throw new InvalidOperationException("送信専用の使用権では受信できません。")).Reader.ReadAsync(cancellationToken);

    /// <summary>共有ソケットから送信し、この送信だけのブロードキャスト設定を適用します。</summary>
    /// <returns>ソケットが受け付けたバイト数です。</returns>
    internal ValueTask<int> SendAsync(ReadOnlyMemory<byte> bytes, IPEndPoint destination, bool broadcast, CancellationToken cancellationToken = default)
        => listener.SendAsync(bytes, destination, broadcast, interfaceIndex, cancellationToken);

    /// <summary>最後の利用が終わった時点でソケットを解放します。</summary>
    /// <returns>最後の利用では受信処理の終了まで待ちます。</returns>
    // 抑制理由: IAsyncDisposableの固定署名で、保持した受信タスクを必ずawaitします。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "IAsyncDisposableの固定署名です。"), System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW511", Justification = "終了タスクをロック外でawaitします。")]
    public async ValueTask DisposeAsync()
    {
        Task completion = Task.CompletedTask;
        using (gate.EnterScope())
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            if (channel is not null)
            {
                listener.Subscribers.Remove(channel);
                channel.Writer.TryComplete();
            }
            listener.Owners--;
            if (listener.Owners == 0)
            {
                listener.Dispose();
                completion = listener.Completion;
            }
        }
        await completion.ConfigureAwait(false);
    }

    /// <summary>受信処理だけを共有し、ノードの実行は各購読側へ任せます。</summary>
    private sealed class Listener(string interfaceId, IPEndPoint endpoint) : IDisposable
    {
        private readonly UdpClient client = new(endpoint);
        private readonly SemaphoreSlim sendGate = new(1, 1);
        internal int Owners
        {
            get; set;
        }

        internal IPEndPoint Endpoint => registeredEndpoint ?? (IPEndPoint)client.Client.LocalEndPoint!;
        internal List<Channel<UdpReceiveResult>> Subscribers
        {
            get;
        } = [];

        internal Task Completion
        {
            get; private set;
        } = Task.CompletedTask;

        private IPEndPoint? registeredEndpoint;
        private bool receiving;

        /// <summary>受信タスクを保持し、最後の購読解除で終了を待てるようにします。</summary>
        // 抑制理由: 共有待受のタスクを保持し、最後の購読のDisposeAsyncでawaitします。
        [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW511", Justification = "共有待受の所有タスクをCompletionへ保存します。") ]
        internal void Start(bool receive)
        {
            registeredEndpoint ??= Endpoint;
            if (receive && !receiving)
            {
                receiving = true;
                Completion = ReceiveAsync(CancellationToken.None);
            }
        }

        /// <summary>登録を解除して保留中の受信を起こします。</summary>
        public void Dispose()
        {
            if (registeredEndpoint is { } bound && listeners.GetValueOrDefault((interfaceId, bound)) == this)
            {
                listeners.Remove((interfaceId, bound));
            }
            client.Dispose();
            if (Owners == 0)
            {
                sendGate.Dispose();
            }
        }

        /// <summary>送信中の設定変更を直列化し、別ノードの送信方針を混ぜません。</summary>
        /// <returns>ソケットへの送信結果です。</returns>
        internal async ValueTask<int> SendAsync(ReadOnlyMemory<byte> bytes, IPEndPoint destination, bool broadcast, int? interfaceIndex, CancellationToken cancellationToken = default)
        {
            await sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                client.EnableBroadcast = broadcast;
                if (interfaceIndex is int index)
                {
                    client.Client.SetSocketOption(SocketOptionLevel.IP, UnicastInterface, IPAddress.HostToNetworkOrder(index));
                }
                return await client.SendAsync(bytes, destination, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                sendGate.Release();
            }
        }

        /// <summary>各購読へ受信データを複製し、遅い購読だけを明示的に終了します。</summary>
        /// <returns>ソケットが解放されるまでの受信処理です。</returns>
        private async Task ReceiveAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    UdpReceiveResult packet = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                    using (gate.EnterScope())
                    {
                        foreach (Channel<UdpReceiveResult> subscriber in Subscribers)
                        {
                            if (!subscriber.Writer.TryWrite(packet))
                            {
                                subscriber.Writer.TryComplete(new IOException("UDP受信キューが上限に達しました。"));
                            }
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
            {
                using (gate.EnterScope())
                {
                    foreach (Channel<UdpReceiveResult> subscriber in Subscribers)
                    {
                        subscriber.Writer.TryComplete(exception);
                    }
                    using IDisposable closed = this;
                }
            }
        }
    }
}
