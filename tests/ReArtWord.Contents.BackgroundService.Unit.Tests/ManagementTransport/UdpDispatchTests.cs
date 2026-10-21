using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.ManagementTransport;

/// <summary>UDP再送・応答照合・受信処理の独立性を確認します。</summary>
public sealed class UdpDispatchTests
{
    /// <summary>実UDPでも、再送ごとに別の断片が失われた応答を復元できます。</summary>
    /// <returns>欠測した応答を同じ要求の再送で補った結果です。</returns>
    [Fact]
    public async Task ReplyRetransmissionRecoversLostFragmentsAsync()
    {
        TransportPair pair = new(TimeSpan.FromSeconds(2));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = pair.ConfigureAwait(true);
        using UdpClient broadcast = new(new IPEndPoint(IPAddress.Loopback, 0)) { EnableBroadcast = true };
        int calls = 0;
        JsonElement body = JsonSerializer.SerializeToElement(new string('x', 8192));
        pair.Receiver.Received = async (message, _, token) =>
        {
            int parity = (Interlocked.Increment(ref calls) - 1) % 2;
            byte[][] packets = DatagramEncoding.Encode(WireMessage.Reply(message, "receiver", pair.ReceiverId, body), 4194304);
            for (int index = parity; index < packets.Length; index += 2)
            {
                await broadcast.SendAsync(packets[index], new IPEndPoint(IPAddress.Parse("127.255.255.255"), pair.Port), token).ConfigureAwait(false);
            }
        };
        pair.Sender.Start();
        pair.Receiver.Start();
        WireMessage? reply = await pair.Sender.RequestAsync(pair.ToReceiver("read"), pair.ReceiverId).ConfigureAwait(true);
        Assert.NotNull(reply);
        Assert.Equal(body.GetString(), reply.Body.GetString());
        Assert.True(2 <= calls);
    }

    /// <summary>期限後の応答を新しい要求として上位へ渡しません。</summary>
    /// <returns>応答を無視しても、通常の要求と応答は継続できることの確認です。</returns>
    [Fact]
    public async Task OrphanReplyIsNotDispatchedAsARequestAsync()
    {
        TransportPair pair = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = pair.ConfigureAwait(true);
        TaskCompletionSource unexpected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.Sender.Received = (_, _, _) =>
        {
            unexpected.TrySetResult();
            return Task.CompletedTask;
        };
        pair.Receiver.Received = (message, _, token) => pair.Receiver.SendAsync(WireMessage.Reply(message, "receiver", pair.ReceiverId, JsonSerializer.SerializeToElement(true)), token);
        pair.Sender.Start();
        pair.Receiver.Start();
        await pair.Receiver.SendAsync(WireMessage.Reply(pair.ToReceiver("expired"), "receiver", pair.ReceiverId, JsonSerializer.SerializeToElement(true))).ConfigureAwait(true);
        WireMessage? reply = await pair.Sender.RequestAsync(pair.ToReceiver("current"), pair.ReceiverId).ConfigureAwait(true);
        Assert.NotNull(reply);
        await Task.WhenAny(unexpected.Task, Task.Delay(100)).ConfigureAwait(true);
        Assert.False(unexpected.Task.IsCompleted);
    }

    /// <summary>別実体の応答が先着しても、確認中の要求を終了させません。</summary>
    /// <returns>実体の一致した応答だけで完了した結果です。</returns>
    [Fact]
    public async Task UnexpectedResponderCannotConsumePendingReplyAsync()
    {
        TransportPair pair = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = pair.ConfigureAwait(true);
        pair.Receiver.Received = async (message, _, token) =>
        {
            await pair.Receiver.SendAsync(WireMessage.Reply(message, "receiver", Guid.NewGuid(), JsonSerializer.SerializeToElement(false)), token).ConfigureAwait(false);
            await Task.Delay(50, token).ConfigureAwait(false);
            await pair.Receiver.SendAsync(WireMessage.Reply(message, "receiver", pair.ReceiverId, JsonSerializer.SerializeToElement(true)), token).ConfigureAwait(false);
        };
        pair.Sender.Start();
        pair.Receiver.Start();
        WireMessage? reply = await pair.Sender.RequestAsync(pair.ToReceiver("read"), pair.ReceiverId).ConfigureAwait(true);
        Assert.NotNull(reply);
        Assert.True(reply.Body.GetBoolean());
    }

    /// <summary>遅い照会の再配送がコールバック枠を増幅せず、完了応答が返ります。</summary>
    /// <returns>同じ配送IDの実UDP再送を一つの実処理へまとめた結果です。</returns>
    [Fact]
    public async Task RetransmissionsShareTheActiveHandlerAsync()
    {
        TransportPair pair = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = pair.ConfigureAwait(true);
        int calls = 0;
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.Receiver.Received = async (message, _, token) =>
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(850, token).ConfigureAwait(false);
            await pair.Receiver.SendAsync(new(Guid.NewGuid(), pair.Cluster, "receiver", pair.ReceiverId, "reply", "sender", message.Id, JsonSerializer.SerializeToElement(true), pair.SenderId), token).ConfigureAwait(false);

            // 応答が届くまでhandlerをactiveに保ち、完了後の再配送との競合を試験対象へ混ぜません。
            await release.Task.WaitAsync(token).ConfigureAwait(false);
        };
        pair.Sender.Start();
        pair.Receiver.Start();
        WireMessage request = pair.ToReceiver("slow-read");
        try
        {
            WireMessage? reply = await pair.Sender.RequestAsync(request, pair.ReceiverId).ConfigureAwait(true);
            Assert.NotNull(reply);
            Assert.True(reply.Body.GetBoolean());
            Assert.Equal(1, calls);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    /// <summary>新しい通常分割と大きい旧分割を、既存receiverと同じ形式で復元できます。</summary>
    [Theory]
    [InlineData(65536, true)]
    [InlineData(600000, false)]
    public void EncodedPacketsPreserveLegacyFragmentLimits(int size, bool mtuSafe)
    {
        WireMessage message = new(Guid.NewGuid(), "cluster", "sender", Guid.NewGuid(), "read", "target", null, JsonSerializer.SerializeToElement(new string('x', size)));
        byte[][] packets = DatagramEncoding.Encode(message, 4194304);
        Assert.InRange(packets.Length, 1, 512);
        FragmentAssembler assembler = new(4194304, TimeProvider.System);
        byte[]? complete = null;
        foreach (byte[] packet in packets.Reverse())
        {
            if (mtuSafe)
            {
                Assert.InRange(packet.Length, 1, 1472);
            }
            DatagramFragment fragment = JsonSerializer.Deserialize<DatagramFragment>(packet)!;
            Assert.InRange(fragment.Payload.Length, 1, 12000);
            complete = assembler.Add("127.0.0.1", fragment) ?? complete;
        }
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(message), complete);
    }

    /// <summary>一配送の分割パケットを受信開始前に蓄積しても、再送せず本文を復元できます。</summary>
    /// <returns>OS受信キューに対する有限burstの実通信確認です。</returns>
    [Fact]
    public async Task FragmentBurstSurvivesDelayedReceiveLoopAsync()
    {
        TransportPair pair = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = pair.ConfigureAwait(true);
        TaskCompletionSource<WireMessage> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.Receiver.Received = (message, _, _) =>
        {
            received.TrySetResult(message);
            return Task.CompletedTask;
        };
        string payload = new('x', 128 * 1024);
        WireMessage request = pair.ToReceiver("burst") with { Body = JsonSerializer.SerializeToElement(payload) };
        await pair.Sender.SendAsync(request).ConfigureAwait(true);
        pair.Receiver.Start();
        WireMessage observed = await received.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(true);
        Assert.Equal(request.Id, observed.Id);
        Assert.Equal(payload, observed.Body.GetString());
    }

    /// <summary>他PC宛ての応答で先頭または後続が欠けても、自分宛ての要求に応答できます。</summary>
    /// <returns>組立枠が無関係な通信で埋まらないことの実UDP確認です。</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task UnrelatedIncompleteRepliesDoNotBlockRequestsAsync(int deliveredFragment)
    {
        TransportPair pair = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = pair.ConfigureAwait(true);
        using UdpClient broadcast = new(new IPEndPoint(IPAddress.Loopback, 0)) { EnableBroadcast = true };
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        TaskCompletionSource admitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.Receiver.Received = async (message, _, token) =>
        {
            admitted.TrySetResult();
            await pair.Receiver.SendAsync(new(Guid.NewGuid(), pair.Cluster, "receiver", pair.ReceiverId, "reply", "sender", message.Id, JsonSerializer.SerializeToElement(true), pair.SenderId), token).ConfigureAwait(false);
        };
        for (int index = 0; index < 40; index++)
        {
            WireMessage foreign = pair.ToReceiver("reply") with { TargetId = "another-peer", Body = JsonSerializer.SerializeToElement(new string('x', 4096)) };
            byte[][] packets = DatagramEncoding.Encode(foreign, 4194304);
            await broadcast.SendAsync(packets[deliveredFragment], new IPEndPoint(IPAddress.Parse("127.255.255.255"), pair.Port), timeout.Token).ConfigureAwait(true);
        }
        pair.Sender.Start();
        pair.Receiver.Start();
        WireMessage request = pair.ToReceiver("management-probe") with { Body = JsonSerializer.SerializeToElement(new string('m', 4096)) };
        WireMessage? reply = await pair.Sender.RequestAsync(request, pair.ReceiverId, timeout.Token).ConfigureAwait(true);
        Assert.NotNull(reply);
        Assert.True(admitted.Task.IsCompletedSuccessfully);
        Assert.True(reply.Body.GetBoolean());
    }

    /// <summary>同期ブロック中も別メッセージと返信を受信し、停止は実コールバック終了を待ちます。</summary>
    /// <returns>実UDPの独立受信と停止完了の確認です。</returns>
    [Fact]
    public async Task SynchronousCallbackDoesNotBlockMessagesOrRepliesAsync()
    {
        TransportPair pair = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = pair.ConfigureAwait(true);
        using ManualResetEventSlim release = new(false);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource heartbeat = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken callbackToken = default;
        bool callbackCompleted = false;
        pair.Receiver.Received = (message, _, token) =>
        {
            string blocked = "blocked";
            if (string.Equals(message.Kind, blocked, StringComparison.Ordinal))
            {
                callbackToken = token;
                entered.TrySetResult();
                release.Wait(CancellationToken.None);
                callbackCompleted = true;
            }
            else
            {
                heartbeat.TrySetResult();
            }
            return Task.CompletedTask;
        };
        pair.Sender.Received = (message, _, token) => pair.Sender.SendAsync(new(Guid.NewGuid(), pair.Cluster, "sender", pair.SenderId, "reply", "receiver", message.Id, JsonSerializer.SerializeToElement(new { confirmed = true }), pair.ReceiverId), token);
        pair.Receiver.Start();
        pair.Sender.Start();
        Task stopping = Task.CompletedTask;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        try
        {
            await pair.Sender.SendAsync(pair.ToReceiver("blocked"), timeout.Token).ConfigureAwait(true);
            await entered.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
            await pair.Sender.SendAsync(pair.ToReceiver("heartbeat"), timeout.Token).ConfigureAwait(true);
            await heartbeat.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
            WireMessage request = new(Guid.NewGuid(), pair.Cluster, "receiver", pair.ReceiverId, "read", "sender", null, JsonSerializer.SerializeToElement(new { }), pair.SenderId);
            WireMessage? reply = await pair.Receiver.RequestAsync(request, pair.SenderId, timeout.Token).ConfigureAwait(true);
            Assert.NotNull(reply);
            Assert.Equal(request.Id, reply.ReplyTo);
            Assert.False(callbackCompleted);
            stopping = pair.Receiver.StopAsync();
            Assert.False(stopping.IsCompleted);
            Assert.True(callbackToken.IsCancellationRequested);
        }
        finally
        {
            release.Set();
            await stopping.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        }
        Assert.True(callbackCompleted);
        await pair.Receiver.StopAsync().ConfigureAwait(true);
    }

    /// <summary>同期完了を繰り返しても登録・cleanup競合で上限や停止を詰まらせません。</summary>
    /// <returns>64件を超える連続完了の確認です。</returns>
    [Fact]
    public async Task SynchronousCompletionsReleaseHandlerCapacityAsync()
    {
        TransportPair pair = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = pair.ConfigureAwait(true);
        Channel<int> completed = Channel.CreateUnbounded<int>();
        pair.Receiver.Received = (message, _, _) =>
        {
            completed.Writer.TryWrite(message.Body.GetInt32());
            return Task.CompletedTask;
        };
        pair.Receiver.Start();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        for (int index = 0; index < 80; index++)
        {
            WireMessage message = pair.ToReceiver("probe") with { Body = JsonSerializer.SerializeToElement(index) };
            await pair.Sender.SendAsync(message, timeout.Token).ConfigureAwait(true);
            Assert.Equal(index, await completed.Reader.ReadAsync(timeout.Token).ConfigureAwait(true));
        }
        await pair.Receiver.StopAsync(timeout.Token).ConfigureAwait(true);
    }

    /// <summary>外部NIC設定を変更しない二つのループバック endpointです。</summary>
    private sealed class TransportPair : IAsyncDisposable
    {
        /// <summary>動的ポートと実ループバック NICを固定します。</summary>
        internal TransportPair(TimeSpan? requestTimeout = null)
        {
            using Socket reservation = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            reservation.Bind(new IPEndPoint(IPAddress.Any, 0));
            int port = ((IPEndPoint)reservation.LocalEndPoint!).Port;
            Port = port;
            reservation.Close();
            string network = NetworkInterface.GetAllNetworkInterfaces().Single(item => item.GetIPProperties().UnicastAddresses.Any(address => address.Address.Equals(IPAddress.Loopback))).Id;
            Sender = new(new("sender", "127.0.0.1", "127.255.255.255", port, Cluster) { RequestTimeout = requestTimeout ?? TimeSpan.FromSeconds(10) }, SenderId, TimeProvider.System);
            Receiver = new(new("receiver", "127.0.0.1", "127.255.255.255", port, Cluster), ReceiverId, TimeProvider.System);
            Sender.Initialize(network);
            Receiver.Initialize(network);
        }

        internal string Cluster
        {
            get;
        } = Guid.NewGuid().ToString("N");

        internal int Port
        {
            get;
        }

        internal Guid SenderId
        {
            get;
        } = Guid.NewGuid();

        internal Guid ReceiverId
        {
            get;
        } = Guid.NewGuid();

        internal UdpTransport Sender
        {
            get;
        }

        internal UdpTransport Receiver
        {
            get;
        }

        /// <summary>受信先実体を固定した一配送です。</summary>
        /// <returns>副作用を含まない試験メッセージです。</returns>
        internal WireMessage ToReceiver(string kind) => new(Guid.NewGuid(), Cluster, "sender", SenderId, kind, "receiver", null, JsonSerializer.SerializeToElement(new { }), ReceiverId);

        /// <summary>コールバックの完了後に所有ソケットを解放します。</summary>
        /// <returns>二つの実通信の終了です。</returns>
        public async ValueTask DisposeAsync()
        {
            await ((IAsyncDisposable)Receiver).DisposeAsync().ConfigureAwait(false);
            await ((IAsyncDisposable)Sender).DisposeAsync().ConfigureAwait(false);
        }
    }
}
