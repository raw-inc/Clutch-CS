using System.Buffers.Binary;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Desktop;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Desktop;
using ReArtWord.Contents.BackgroundService.Tests.Shared;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>実Core、ClientHost、対話worker、encoderの接続と回収を確認します。</summary>
public sealed class DesktopProcessTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private bool audioFormatSeen;
    private int audioPackets;
    private bool compressedAudio;
    private bool opusAcknowledged;
    private bool cursorExpected;

    /// <summary>選択したホストのH.264/H.265がClientHostへ中継され、切断でsessionが回収されます。</summary>
    /// <param name="codec">実際に取得・符号化する映像形式です。</param>
    /// <param name="opus">圧縮音声を選択するかです。</param>
    /// <param name="bFrames">-1は通常設定、それ以外は詳細設定の連続B上限です。</param>
    /// <param name="variableBitrate">VBRを固定長入力で指定するかです。</param>
    /// <returns>実プロセスと実WebSocketによる確認です。</returns>
    [HostedDesktopTheory, InlineData(EDesktopVideoCodec.H264, false, -1, false), InlineData(EDesktopVideoCodec.H264, true, -1, false), InlineData(EDesktopVideoCodec.H265, false, -1, false),
     InlineData(EDesktopVideoCodec.H264, false, 0, false), InlineData(EDesktopVideoCodec.H264, false, 2, false), InlineData(EDesktopVideoCodec.H265, false, 2, false),
     InlineData(EDesktopVideoCodec.H264, false, -1, true), InlineData(EDesktopVideoCodec.H265, false, -1, true)]
    public async Task WindowsDesktopFlowsThroughClientHostAndDisconnectReclaimsSession(EDesktopVideoCodec codec, bool opus, int bFrames, bool variableBitrate)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        string cluster = Guid.NewGuid().ToString("N");
        compressedAudio = opus;
        int port = CorePeer.AvailablePort();
        CorePeer core = new(cluster, "desktop", port, true, "desktop-host");
        string? ffmpeg = Environment.GetEnvironmentVariable("CONTENTS_TEST_FFMPEG");
        if (!string.IsNullOrWhiteSpace(ffmpeg))
        {
            await File.AppendAllTextAsync(Path.Combine(core.DirectoryPath, "contents.toml"), "\n[media]\nffmpegPath = " + JsonSerializer.Serialize(ffmpeg.Replace('\\', '/')) + "\n").ConfigureAwait(true);
        }
        ClientHostProcess client = new(Path.Combine(core.DirectoryPath, "client"));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable coreLifetime = ((IAsyncDisposable)core).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable clientLifetime = ((IAsyncDisposable)client).ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
        await core.StartAsync(timeout.Token).ConfigureAwait(true);
        await client.StartAsync(timeout.Token).ConfigureAwait(true);
        using (HttpResponseMessage forbidden = await client.SendAsync(HttpMethod.Get, "client/desktop?host=desktop-host", cancellationToken: timeout.Token).ConfigureAwait(true))
        {
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, forbidden.StatusCode);
        }
        using (HttpResponseMessage configured = await client.SendAsync(HttpMethod.Post, "client/network", new ClientNetworkBinding(core.InterfaceId, "127.0.0.1", "127.255.255.255", port), cancellationToken: timeout.Token).ConfigureAwait(true))
        {
            configured.EnsureSuccessStatusCode();
        }
        await CorePeer.WaitAsync(async token =>
        {
            using HttpResponseMessage response = await client.SendAsync(HttpMethod.Get, "client/clusters", cancellationToken: token).ConfigureAwait(true);
            DiscoveredCluster[] values = (await response.Content.ReadFromJsonAsync<DiscoveredCluster[]>(ContractJson.Options, token).ConfigureAwait(true))!;
            return values.Any(value => string.Equals(value.ClusterId, cluster, StringComparison.Ordinal) && value.Peers.Any(peer => string.Equals(peer.HostId, "desktop-host", StringComparison.Ordinal) && peer.Status == Contracts.Cluster.EPeerStatus.Ready));
        }, timeout.Token).ConfigureAwait(true);
        using ClientWebSocket socket = new();
        socket.Options.SetRequestHeader("Origin", client.Address.GetLeftPart(UriPartial.Authority));
        UriBuilder uri = new(client.Address) { Scheme = "ws", Path = "/client/desktop", Query = "host=desktop-host" };
        await socket.ConnectAsync(uri.Uri, timeout.Token).ConfigureAwait(true);
        byte[] buffer = new byte[DesktopWire.MaximumFrameBytes + DesktopWire.HeaderSize];
        int count = await ReceiveAsync(socket, buffer, timeout.Token).ConfigureAwait(true);
        Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(buffer));
        DesktopMonitor[] monitors = JsonSerializer.Deserialize<DesktopMonitor[]>(buffer.AsSpan(DesktopWire.HeaderSize, count - DesktopWire.HeaderSize), ContractJson.Options)!;
        Assert.NotEmpty(monitors);
        output.WriteLine("Monitors: " + JsonSerializer.Serialize(monitors));
        Assert.All(monitors, monitor =>
        {
            Assert.InRange(monitor.Width, 1, 32768);
            Assert.InRange(monitor.Height, 1, 32768);
        });

        // 新Coreへの明示購読を、映像開始前に実TCP/WebSocket経路で確認します。
        // ブラウザーでの旧版との能力交渉はClient側の試験で別途確認します。
        byte[] inputMode = new byte[DesktopWire.InputSize];
        BinaryPrimitives.WriteInt32BigEndian(inputMode, 11);
        BinaryPrimitives.WriteInt32BigEndian(inputMode.AsSpan(4), 1);
        await socket.SendAsync(inputMode, WebSocketMessageType.Binary, true, timeout.Token).ConfigureAwait(true);
        byte[] cursorSelection = new byte[DesktopWire.InputSize];
        BinaryPrimitives.WriteInt32BigEndian(cursorSelection, 8);
        BinaryPrimitives.WriteInt32BigEndian(cursorSelection.AsSpan(4), 1);
        cursorExpected = true;
        await socket.SendAsync(cursorSelection, WebSocketMessageType.Binary, true, timeout.Token).ConfigureAwait(true);
        await ReceiveAsync(socket, buffer, timeout.Token).ConfigureAwait(true);
        Assert.Equal(11, BinaryPrimitives.ReadInt32BigEndian(buffer));
        using DesktopSilentPlayback playback = new(Path.Combine(core.DirectoryPath, "silence.wav"));
        if (opus)
        {
            byte[] audioSelection = new byte[DesktopWire.InputSize];
            BinaryPrimitives.WriteInt32BigEndian(audioSelection, 7);
            BinaryPrimitives.WriteInt32BigEndian(audioSelection.AsSpan(4), 1);
            BinaryPrimitives.WriteInt32BigEndian(audioSelection.AsSpan(8), 192);
            await socket.SendAsync(audioSelection, WebSocketMessageType.Binary, true, timeout.Token).ConfigureAwait(true);
        }
        byte[] details = new byte[DesktopWire.InputSize];
        BinaryPrimitives.WriteInt32BigEndian(details, 9);
        if (0 <= bFrames)
        {
            int[] values = [9, 1, 12, -1, bFrames, 23];
            for (int i = 0; i < values.Length; i++)
            {
                BinaryPrimitives.WriteInt32BigEndian(details.AsSpan(i * 4), values[i]);
            }
            await socket.SendAsync(details, WebSocketMessageType.Binary, true, timeout.Token).ConfigureAwait(true);
        }
        byte[] selection = new byte[DesktopWire.InputSize];
        BinaryPrimitives.WriteInt32BigEndian(selection, 6);
        BinaryPrimitives.WriteInt32BigEndian(selection.AsSpan(4), monitors[0].Id);
        BinaryPrimitives.WriteInt32BigEndian(selection.AsSpan(8), (int)codec);
        if (variableBitrate)
        {
            BinaryPrimitives.WriteInt32BigEndian(selection.AsSpan(20), (30 << 20) | 6000 | 0x10000000);
        }
        await socket.SendAsync(selection, WebSocketMessageType.Binary, true, timeout.Token).ConfigureAwait(true);
        count = await ReceiveAsync(socket, buffer, timeout.Token).ConfigureAwait(true);
        Assert.Equal(3, BinaryPrimitives.ReadInt32BigEndian(buffer));
        using (JsonDocument encoders = JsonDocument.Parse(buffer.AsMemory(DesktopWire.HeaderSize, count - DesktopWire.HeaderSize)))
        {
            string[] supported = ["h264_nvenc", "h264_qsv", "h264_amf", "libx264"];
            if (codec == EDesktopVideoCodec.H265)
            {
                supported = ["hevc_nvenc", "hevc_qsv", "hevc_amf", "libx265"];
            }
            Assert.Contains(encoders.RootElement[0].GetProperty("encoder").GetString(), supported);
            Assert.Equal(variableBitrate, encoders.RootElement[0].GetProperty("variableBitrate").GetBoolean());
            Assert.True(encoders.RootElement[0].GetProperty("cursorShapes").GetBoolean());
            Assert.True(encoders.RootElement[0].GetProperty("scanCodeInput").GetBoolean());
            Assert.True(encoders.RootElement[0].GetProperty("unicodeInput").GetBoolean());
            Assert.True(encoders.RootElement[0].GetProperty("cursorVisibility").GetBoolean());
            Assert.False(encoders.RootElement[0].GetProperty("hideCursor").GetBoolean());
            if (0 <= bFrames)
            {
                DesktopCodecSettings? applied = encoders.RootElement[0].GetProperty("codecDetails").Deserialize<DesktopCodecSettings>(ContractJson.Options);
                Assert.Equal(new(12, null, bFrames, 23), applied);
                Assert.Equal(12, encoders.RootElement[0].GetProperty("effectiveKeyFrameInterval").GetInt32());
                Assert.Equal(JsonValueKind.Null, encoders.RootElement[0].GetProperty("maximumBitrateKbps").ValueKind);
            }
            output.WriteLine("Encoders: " + encoders.RootElement);
        }

        // 初期化直後にも実入力を通します。未押下のVK_NONAMEの解放なので、
        // 文字入力や実際のポインター移動を伴いません。
        byte[] release = new byte[DesktopWire.InputSize];
        BinaryPrimitives.WriteInt32BigEndian(release, 4);
        BinaryPrimitives.WriteInt32BigEndian(release.AsSpan(16), 0xfc);
        await socket.SendAsync(release, WebSocketMessageType.Binary, true, timeout.Token).ConfigureAwait(true);

        // 未割当てscan codeの解放だけで、新しい入力形式のnative経路を確認します。
        BinaryPrimitives.WriteInt32BigEndian(release.AsSpan(16), 0x17f);
        await socket.SendAsync(release, WebSocketMessageType.Binary, true, timeout.Token).ConfigureAwait(true);
        count = await ReceiveAsync(socket, buffer, timeout.Token).ConfigureAwait(true);
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(buffer));
        Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(12)));
        Assert.Equal(count - DesktopWire.HeaderSize, DesktopWire.PayloadLength(buffer.AsSpan(0, DesktopWire.HeaderSize)));
        Assert.Equal(new byte[] { 0, 0, 0, 1 }, buffer.AsSpan(DesktopWire.HeaderSize, 4).ToArray());
        BinaryPrimitives.WriteInt32BigEndian(selection.AsSpan(4), -1);
        BinaryPrimitives.WriteInt32BigEndian(selection.AsSpan(20), (30 << 20) | 6000 | 0x08000000);
        details.AsSpan(4).Clear();
        await socket.SendAsync(details, WebSocketMessageType.Binary, true, timeout.Token).ConfigureAwait(true);
        await socket.SendAsync(selection, WebSocketMessageType.Binary, true, timeout.Token).ConfigureAwait(true);
        do
        {
            count = await ReceiveAsync(socket, buffer, timeout.Token).ConfigureAwait(true);
        }
        while (BinaryPrimitives.ReadInt32BigEndian(buffer) != 3);
        using (JsonDocument encoders = JsonDocument.Parse(buffer.AsMemory(DesktopWire.HeaderSize, count - DesktopWire.HeaderSize)))
        {
            Assert.All(encoders.RootElement.EnumerateArray(), encoder => Assert.True(encoder.GetProperty("hideCursor").GetBoolean()));
            Assert.All(encoders.RootElement.EnumerateArray(), encoder => Assert.Equal(JsonValueKind.Null, encoder.GetProperty("codecDetails").ValueKind));
            Assert.All(encoders.RootElement.EnumerateArray(), encoder => Assert.Equal(6000, encoder.GetProperty("maximumBitrateKbps").GetInt32()));
            Assert.All(encoders.RootElement.EnumerateArray(), encoder => Assert.False(encoder.GetProperty("variableBitrate").GetBoolean()));
        }
        HashSet<int> receivedMonitors = [];
        while (receivedMonitors.Count < monitors.Length)
        {
            await ReceiveAsync(socket, buffer, timeout.Token).ConfigureAwait(true);
            Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(buffer));
            int id = BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(4));
            Assert.InRange(id, 0, monitors.Length - 1);
            if (receivedMonitors.Add(id))
            {
                Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(12)));
            }
        }
        output.WriteLine("All monitor streams received: " + receivedMonitors.Count);
        BinaryPrimitives.WriteInt32BigEndian(selection.AsSpan(20), (30 << 20) | 6000);
        await socket.SendAsync(selection, WebSocketMessageType.Binary, true, timeout.Token).ConfigureAwait(true);
        do
        {
            count = await ReceiveAsync(socket, buffer, timeout.Token).ConfigureAwait(true);
        }
        while (BinaryPrimitives.ReadInt32BigEndian(buffer) != 3);
        using (JsonDocument encoders = JsonDocument.Parse(buffer.AsMemory(DesktopWire.HeaderSize, count - DesktopWire.HeaderSize)))
        {
            Assert.All(encoders.RootElement.EnumerateArray(), encoder => Assert.False(encoder.GetProperty("hideCursor").GetBoolean()));
        }
        Assert.True(audioFormatSeen);
        Assert.True(0 < audioPackets);
        output.WriteLine("Audio packets received: " + audioPackets);
        socket.Abort();
        await CorePeer.WaitAsync(async token => await core.GetAsync<int>("desktop.status", token).ConfigureAwait(true) == 0, timeout.Token).ConfigureAwait(true);
    }

    /// <summary>断片化された一つのWebSocket messageだけを読みます。</summary>
    /// <returns>受信した長さです。</returns>
    private async Task<int> ReceiveAsync(ClientWebSocket socket, byte[] buffer, CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            int offset = 0;
            ValueWebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(true);
                Assert.True(result.MessageType == WebSocketMessageType.Binary,
                    $"Unexpected {result.MessageType}: {socket.CloseStatus} / {socket.CloseStatusDescription}");
                offset += result.Count;
            }
            while (!result.EndOfMessage);
            int kind = BinaryPrimitives.ReadInt32BigEndian(buffer);
            if (kind == 8)
            {
                Assert.Fail("Desktop worker fault: " + System.Text.Encoding.UTF8.GetString(buffer, DesktopWire.HeaderSize, offset - DesktopWire.HeaderSize));
            }
            if (kind == 7)
            {
                using JsonDocument connection = JsonDocument.Parse(buffer.AsMemory(24, offset - 24));
                Assert.True(connection.RootElement.GetProperty("localAudioMuted").GetBoolean());
                Assert.True(connection.RootElement.GetProperty("cursorShapes").GetBoolean());
                Assert.True(connection.RootElement.GetProperty("codecDetails").GetBoolean());
                Assert.True(connection.RootElement.GetProperty("viewOnly").GetBoolean());
            }
            if (kind == 11)
            {
                Assert.Equal(4, DesktopWire.PayloadLength(buffer.AsSpan(0, 24)));
                Assert.InRange(BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(24)), 0, (int)EDesktopCursorShape.Hidden);
                if (cursorExpected)
                {
                    cursorExpected = false;
                    return offset;
                }
            }
            if (kind == 4)
            {
                using JsonDocument format = JsonDocument.Parse(buffer.AsMemory(24, offset - 24));
                Assert.Equal(48000, format.RootElement.GetProperty("sampleRate").GetInt32());
                Assert.Equal(2, format.RootElement.GetProperty("channels").GetInt32());
                string opusFormat = "opus";
                string? receivedFormat = format.RootElement.GetProperty("format").GetString();
                if (string.Equals(receivedFormat, opusFormat, StringComparison.Ordinal))
                {
                    Assert.True(compressedAudio);
                    Assert.Equal(192, format.RootElement.GetProperty("bitrateKbps").GetInt32());
                    opusAcknowledged = true;
                }
                audioFormatSeen = true;
            }
            if (kind is 5 or 6)
            {
                Assert.True(audioFormatSeen);
                Assert.Equal(0, (offset - 24) % 8);
                Assert.True(0 < BinaryPrimitives.ReadInt64BigEndian(buffer.AsSpan(16)));
                if (!compressedAudio)
                {
                    audioPackets++;
                }
            }
            if (kind == 10)
            {
                Assert.True(opusAcknowledged);
                Assert.Equal(offset - 24, DesktopWire.PayloadLength(buffer.AsSpan(0, 24)));
                int frames = BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(24));
                int skip = BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(28));
                Assert.InRange(frames, 1, 960 - skip);
                audioPackets++;
            }
            if (kind is not (4 or 5 or 6 or 7 or 10 or 11))
            {
                return offset;
            }
        }
        throw new OperationCanceledException(cancellationToken);
    }
}
