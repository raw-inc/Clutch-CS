using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Runtime.Networking;


namespace ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

/// <summary>同じメッセージの再配送を副作用実行から分離するUDP通信です。</summary>
public sealed class UdpTransport : IAsyncDisposable
{
    private readonly DatagramTransportOptions options;
    private readonly Guid instanceId;
    private readonly TimeProvider clock;
    private DateTimeOffset bindingValidUntil;
    private int interfaceIndex;
    private UdpClient? sender;
    private readonly Lock bindingGate = new();
    private readonly UdpClient receiver;
    private readonly FragmentAssembler assembler;
    private LocalNetworkBinding? binding;
    private readonly CancellationTokenSource stopping = new();
    private readonly ConcurrentDictionary<Guid, PendingDatagramReply> replies = [];
    private readonly ConcurrentDictionary<(string Address, Guid Id), Task> handlers = [];
    private readonly Lazy<Task> receiveTask;

    /// <summary>指定IPv4だけから送信する通信を構成します。</summary>
    public UdpTransport(DatagramTransportOptions options, Guid instanceId, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        this.options = options;
        this.instanceId = instanceId;
        this.clock = clock;
        IPAddress address = IPAddress.Parse(options.BindAddress);
        if (address.AddressFamily != AddressFamily.InterNetwork || address.Equals(IPAddress.Any))
        {
            throw new ArgumentException("明示したIPv4 interfaceが必要です。", nameof(options));
        }
        receiver = new UdpClient(AddressFamily.InterNetwork);

        // 分割配送と返信のburstを小さいOS既定キューで失わないよう、受信待機を有限に確保します。
        receiver.Client.ReceiveBufferSize = 1024 * 1024;
        receiver.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        receiver.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
        receiver.Client.Bind(new IPEndPoint(IPAddress.Any, options.Port));
        assembler = new FragmentAssembler(options.MaximumMessageBytes, clock, new DatagramRecipient(options.SenderId, instanceId, options.ClusterId));
        receiveTask = new Lazy<Task>(() => ReceiveAsync(stopping.Token));
    }

    /// <summary>受信した完全メッセージの処理です。</summary>
    public Func<WireMessage, string, CancellationToken, Task>? Received
    {
        get; set;
    }

    /// <summary>実際に固定したNIC識別子です。</summary>
    public string InterfaceId
    {
        get; private set;
    } = string.Empty;

    /// <summary>同一NICとIPの現在の成立状態です。</summary>
    public bool BindingAvailable
    {
        get
        {
            try
            {
                ValidateBinding();
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }
    }

    /// <summary>参加管理とは独立に、明示したNICの同一性を固定します。</summary>
    public void Initialize(string interfaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interfaceId);
        if (binding is not null)
        {
            throw new InvalidOperationException("通信割当は初期化済みです。");
        }
        InterfaceId = interfaceId;
        binding = new LocalNetworkBinding(interfaceId, options.BindAddress);
        NetworkChange.NetworkAddressChanged += BindingChanged;
        NetworkChange.NetworkAvailabilityChanged += BindingChanged;
    }

    /// <summary>固定したNICとIPが現在も成立することを確認します。</summary>
    public void ValidateBinding()
    {
        using (bindingGate.EnterScope())
        {
            if (clock.GetUtcNow() < bindingValidUntil)
            {
                return;
            }
            try
            {
                NetworkInterface network = binding?.ResolveInterface(useObservation: true) ?? throw new SocketException((int)SocketError.AddressNotAvailable);
                interfaceIndex = network.GetIPProperties().GetIPv4Properties().Index;

                // 構成変更通知で即座に無効化し、通知されない物理断も定期観測で再検査します。
                bindingValidUntil = clock.GetUtcNow().AddSeconds(1);
                return;
            }
            catch (Exception exception) when (exception is SocketException or NetworkInformationException)
            {
                using UdpClient? previous = sender;
                sender = null;
            }
        }
        throw new SocketException((int)SocketError.AddressNotAvailable);
    }

    /// <summary>OSの構成変更通知で次の送受信前の実NIC再検証を要求します。</summary>
    private void BindingChanged(object? sender, EventArgs args)
    {
        using (bindingGate.EnterScope())
        {
            bindingValidUntil = DateTimeOffset.MinValue;
        }
    }

    /// <summary>追加Ready条件の前に固定通信条件を必ず確認します。</summary>
    /// <returns>両方の実条件が成立するか。</returns>
    public async ValueTask<bool> CheckReadyAsync(Func<CancellationToken, ValueTask<bool>>? check, CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateBinding();
            return check is not null && await check(cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>同一NICの復帰後にだけ送信ソケットを再作成します。</summary>
    /// <returns>固定IPの送信ソケットです。</returns>
    private UdpClient Sender()
    {
        using (bindingGate.EnterScope())
        {
            sender ??= new UdpClient(new IPEndPoint(IPAddress.Parse(options.BindAddress), 0)) { EnableBroadcast = true };
            return sender;
        }
    }

    /// <summary>受信ループを開始します。</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(stopping.IsCancellationRequested, this);
        if (receiveTask.Value.IsFaulted)
        {
            throw new InvalidOperationException("受信処理を開始できません。");
        }
    }

    /// <summary>同じ配送IDを再送し、相手実体の応答を待ちます。</summary>
    /// <returns>確認応答。期限内に確認できなければnull。</returns>
    public async Task<WireMessage?> RequestAsync(WireMessage message, Guid targetInstanceId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        TaskCompletionSource<WireMessage> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!replies.TryAdd(message.Id, new(message, targetInstanceId, completion)))
        {
            throw new InvalidOperationException("同じ配送IDが進行中です。");
        }
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping.Token);
        timeout.CancelAfter(options.RequestTimeout);
        try
        {
            byte[][] packets = DatagramEncoding.Encode(message, options.MaximumMessageBytes);
            while (!timeout.IsCancellationRequested)
            {
                await SendPacketsAsync(packets, options.Port, timeout.Token).ConfigureAwait(false);
                if (ReferenceEquals(await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromMilliseconds(250), timeout.Token)).ConfigureAwait(false), completion.Task))
                {
                    return await completion.Task.ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            replies.TryRemove(message.Id, out _);
        }
        return null;
    }

    /// <summary>分割して明示ブロードキャスト先へ送ります。</summary>
    /// <returns>送信処理の完了。到達保証ではありません。</returns>
    public Task SendAsync(WireMessage message, CancellationToken cancellationToken = default) => SendToPortAsync(message, options.Port, cancellationToken);

    /// <summary>同じNIC上の指定ポートへ通知を送信します。</summary>
    /// <returns>送信処理の完了です。</returns>
    public async Task SendToPortAsync(WireMessage message, int port, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        await SendPacketsAsync(DatagramEncoding.Encode(message, options.MaximumMessageBytes), port, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>固定した分割内容を再利用し、送信ごとにNIC成立条件を検査します。</summary>
    /// <returns>到達保証と区別した送信の完了です。</returns>
    private async Task SendPacketsAsync(byte[][] packets, int port, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(stopping.IsCancellationRequested, this);
        ValidateBinding();
        IPEndPoint destination = new(IPAddress.Parse(options.BroadcastAddress), port);
        int packetsPerBurst = 16;
        int sent = 0;
        foreach (byte[] packet in packets)
        {
            await Sender().SendAsync(packet, destination, cancellationToken).ConfigureAwait(false);
            sent++;

            // 多数の断片を一気に送らず、受信側が取り出す時間を確保します。
            if (sent % packetsPerBurst == 0 && sent < packets.Length)
            {
                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>新たな受信を止め、登録コールバックの終了を待ちます。</summary>
    /// <returns>上位の実サービスを解放できる状態です。</returns>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await stopping.CancelAsync().ConfigureAwait(false);
        if (receiveTask.IsValueCreated)
        {
            await receiveTask.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        await Task.WhenAll(handlers.Values).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>通信と追跡中の受信処理を終了します。</summary>
    /// <returns>停止の完了。</returns>
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        NetworkChange.NetworkAddressChanged -= BindingChanged;
        NetworkChange.NetworkAvailabilityChanged -= BindingChanged;
        receiver.Dispose();
        sender?.Dispose();
        stopping.Dispose();
    }

    /// <summary>不正・他クラスタ・別宛先パケットを除き受信します。</summary>
    /// <returns>受信ループの終了。</returns>
    private async Task ReceiveAsync(CancellationToken cancellationToken = default)
    {
        byte[] buffer = new byte[20000];
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                SocketReceiveMessageFromResult datagram = await receiver.Client.ReceiveMessageFromAsync(buffer.AsMemory(), SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cancellationToken).ConfigureAwait(false);
                ValidateBinding();
                if (datagram.PacketInformation.Interface != interfaceIndex || datagram.SocketFlags.HasFlag(SocketFlags.Truncated))
                {
                    continue;
                }
                string address = ((IPEndPoint)datagram.RemoteEndPoint).Address.ToString();
                DatagramFragment? fragment = assembler.Decode(address, buffer.AsSpan(0, datagram.ReceivedBytes));
                if (fragment is null)
                {
                    continue;
                }
                byte[]? body = assembler.Add(address, fragment);
                WireMessage? message = null;
                if (body is not null)
                {
                    message = JsonSerializer.Deserialize<WireMessage>(body, ContractJson.Options);
                }
                if (message is null || message.InstanceId == instanceId || (message.TargetInstanceId.HasValue && message.TargetInstanceId != instanceId) || (options.ClusterId is not null && !string.Equals(message.ClusterId, options.ClusterId, StringComparison.Ordinal)) || (message.TargetId is not null && !string.Equals(message.TargetId, options.SenderId, StringComparison.Ordinal)))
                {
                    continue;
                }
                if (message.ReplyTo is Guid replyId)
                {
                    if (replies.TryGetValue(replyId, out PendingDatagramReply? reply) && reply.Matches(message))
                    {
                        reply.Completion.TrySetResult(message);
                    }

                    // 期限後や別実体の応答を、新しい要求として上位へ渡しません。
                }
                else
                {
                    if (Received is not null)
                    {
                        (string Address, Guid Id) invocation = (address, message.Id);
                        if (handlers.ContainsKey(invocation) || 64 <= handlers.Count)
                        {
                            continue;
                        }
                        TaskCompletionSource registered = new(TaskCreationOptions.RunContinuationsAsynchronously);

                        // 同期完了時のcleanupが追跡登録を追い越さないよう、登録後に処理を開放します。
                        handlers.TryAdd(invocation, Task.Run(async () =>
                        {
                            await registered.Task.ConfigureAwait(false);
                            await HandleAsync(invocation, message, address, cancellationToken).ConfigureAwait(false);
                        }, CancellationToken.None));

                        registered.SetResult();
                    }
                }
            }
            catch (Exception exception) when (exception is JsonException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidDataException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    /// <summary>長い要求でハートビート受信を止めないよう処理を追跡します。</summary>
    /// <returns>処理の完了。</returns>
    private async Task HandleAsync((string Address, Guid Id) invocation, WireMessage message, string address, CancellationToken cancellationToken = default)
    {
        try
        {
            if (Received is not null)
            {
                await Received(message, address, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // 処理結果が作れない場合はACKを返さず、依頼元の確認不能として扱います。
        }
        finally
        {
            handlers.TryRemove(invocation, out _);
        }
    }

}
