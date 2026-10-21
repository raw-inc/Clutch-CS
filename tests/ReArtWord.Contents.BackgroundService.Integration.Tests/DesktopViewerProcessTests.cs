using System.Buffers.Binary;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Desktop;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>実CoreとClientHostが、閲覧端末の所在地をWebSocketの接続情報へ反映することを確認します。</summary>
public sealed class DesktopViewerProcessTests
{
    /// <summary>CoreとClientHostが同居していても、Tunnelの閲覧者を自己接続にしません。</summary>
    /// <returns>音声・入力の循環抑止フラグとsession回収の確認です。</returns>
    [Theory]
    [InlineData(null, true, false)]
    [InlineData("198.51.100.20", false, false)]
    [InlineData("", false, false)]
    [InlineData(null, true, true)]
    public async Task ConnectionDescribesViewerInsteadOfRelayAsync(string? forwarded, bool local, bool timing)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer core = new(cluster, "desktop", port, true, "viewer-test-host");
        ClientHostProcess client = new(Path.Combine(core.DirectoryPath, "client"));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable coreLifetime = ((IAsyncDisposable)core).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable clientLifetime = ((IAsyncDisposable)client).ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(45));
        await core.StartAsync(timeout.Token).ConfigureAwait(true);
        await client.StartAsync(timeout.Token).ConfigureAwait(true);
        using (HttpResponseMessage configured = await client.SendAsync(HttpMethod.Post, "client/network", new ClientNetworkBinding(core.InterfaceId, "127.0.0.1", "127.255.255.255", port), cancellationToken: timeout.Token).ConfigureAwait(true))
        {
            configured.EnsureSuccessStatusCode();
        }
        await CorePeer.WaitAsync(async token =>
        {
            using HttpResponseMessage response = await client.SendAsync(HttpMethod.Get, "client/clusters", cancellationToken: token).ConfigureAwait(true);
            DiscoveredCluster[] values = (await response.Content.ReadFromJsonAsync<DiscoveredCluster[]>(ContractJson.Options, token).ConfigureAwait(true))!;
            return values.Any(value => string.Equals(value.ClusterId, cluster, StringComparison.Ordinal)
                && value.Peers.Any(peer => peer.Status == Contracts.Cluster.EPeerStatus.Ready));
        }, timeout.Token).ConfigureAwait(true);
        using ClientWebSocket socket = new();
        string origin = client.Address.GetLeftPart(UriPartial.Authority);
        if (forwarded is not null)
        {
            origin = "https://desktop.example";
            socket.Options.SetRequestHeader("X-Forwarded-Proto", "https");
            socket.Options.SetRequestHeader("X-Forwarded-Host", "desktop.example");
            if (forwarded.Length != 0)
            {
                socket.Options.SetRequestHeader("X-Forwarded-For", forwarded);
            }
        }
        socket.Options.SetRequestHeader("Origin", origin);
        UriBuilder uri = new(client.Address) { Scheme = "ws", Path = "/client/desktop", Query = "host=viewer-test-host&cluster=" + cluster };
        if (timing)
        {
            uri.Query += "&timing=1";
        }
        await socket.ConnectAsync(uri.Uri, timeout.Token).ConfigureAwait(true);
        byte[] message = new byte[65560];
        int count = 0;
        ValueWebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(message.AsMemory(count), timeout.Token).ConfigureAwait(true);
            Assert.Equal(WebSocketMessageType.Binary, received.MessageType);
            count += received.Count;
        }
        while (!received.EndOfMessage);
        Assert.Equal(7, BinaryPrimitives.ReadInt32BigEndian(message));
        Assert.Equal(count - DesktopWire.HeaderSize, DesktopWire.PayloadLength(message.AsSpan(0, DesktopWire.HeaderSize)));
        using JsonDocument description = JsonDocument.Parse(message.AsMemory(DesktopWire.HeaderSize, count - DesktopWire.HeaderSize));
        Assert.Equal(local, description.RootElement.GetProperty("localAudioMuted").GetBoolean());
        Assert.Equal("viewer-test-host", description.RootElement.GetProperty("hostId").GetString());
        Assert.Equal(timing, description.RootElement.GetProperty("timing").GetBoolean());
        if (timing)
        {
            byte[] probe = new byte[24];
            BinaryPrimitives.WriteInt32BigEndian(probe, 14);
            BinaryPrimitives.WriteInt32BigEndian(probe.AsSpan(4), 42);
            BinaryPrimitives.WriteInt64BigEndian(probe.AsSpan(8), 123456);
            await socket.SendAsync(probe, WebSocketMessageType.Binary, true, timeout.Token).ConfigureAwait(true);
            do
            {
                count = 0;
                do
                {
                    received = await socket.ReceiveAsync(message.AsMemory(count), timeout.Token).ConfigureAwait(true);
                    Assert.Equal(WebSocketMessageType.Binary, received.MessageType);
                    count += received.Count;
                }
                while (!received.EndOfMessage);
                if (BinaryPrimitives.ReadInt32BigEndian(message) != 13)
                {
                    await socket.SendAsync(probe, WebSocketMessageType.Binary, true, timeout.Token).ConfigureAwait(true);
                }
            }
            while (BinaryPrimitives.ReadInt32BigEndian(message) != 13);
            Assert.Equal(48, count);
            Assert.Equal(42, BinaryPrimitives.ReadInt32BigEndian(message.AsSpan(24)));
            Assert.Equal(123456, BinaryPrimitives.ReadInt64BigEndian(message.AsSpan(32)));
            Assert.InRange(BinaryPrimitives.ReadInt64BigEndian(message.AsSpan(40)), 0, BinaryPrimitives.ReadInt64BigEndian(message.AsSpan(16)));
        }
        socket.Abort();
        await CorePeer.WaitAsync(async token => await core.GetAsync<int>("desktop.status", token).ConfigureAwait(true) == 0, timeout.Token).ConfigureAwait(true);
    }
}
