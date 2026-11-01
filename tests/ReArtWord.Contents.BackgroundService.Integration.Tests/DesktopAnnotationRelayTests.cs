using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
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

/// <summary>異なるclusterの同一PCへ接続し、認証済みTCP・WebSocketを跨ぐ指示共有を検証します。</summary>
public sealed class DesktopAnnotationRelayTests
{
    /// <summary>操作側と閲覧側の所属Coreが異なっても物理座標と色、軌跡を共有します。</summary>
    /// <returns>二つのCore、ClientHost、二つのworkerの実通信です。</returns>
    [HostedDesktopFact(minimumWindows10Build: 19041)]
    public async Task WindowsAnnotationsReachAnotherCoreOnTheSameHostAsync()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            return;
        }
        string cluster = Guid.NewGuid().ToString("N");
        string group = "Local\\Contents.Annotation.Integration." + Guid.NewGuid().ToString("N");
        string groupVariable = "CONTENTS_TEST_ANNOTATION_GROUP";
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string executable = Path.GetFullPath("../../../../ReArtWord.Contents.BackgroundService.Unit.Tests/Management/FixtureHost/bin/"
            + configuration + "/net10.0/ContentsDesktopFixture.exe", AppContext.BaseDirectory);
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster + "a", "a", port, true, "annotation-host", executablePath: executable);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        CorePeer second = new(cluster + "b", "b", port, true, "annotation-host", executablePath: executable);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        ClientHostProcess client = new(Path.Combine(first.DirectoryPath, "client"));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable clientLifetime = client.ConfigureAwait(true);

        // 同じ試験のCoreだけが共有する名前を、子ワーカーにも引き継ぎます。
        first.StartEnvironment[groupVariable] = group;
        second.StartEnvironment[groupVariable] = group;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(45));
        await first.StartAsync(timeout.Token).ConfigureAwait(true); await second.StartAsync(timeout.Token).ConfigureAwait(true); await client.StartAsync(timeout.Token).ConfigureAwait(true);
        using HttpResponseMessage binding = await client.SendAsync(HttpMethod.Post, "client/network", new ClientNetworkBinding(first.InterfaceId, "127.0.0.1", "127.255.255.255", port), cancellationToken: timeout.Token).ConfigureAwait(true);
        binding.EnsureSuccessStatusCode();
        await CorePeer.WaitAsync(async token =>
        {
            using HttpResponseMessage response = await client.SendAsync(HttpMethod.Get, "client/clusters", cancellationToken: token).ConfigureAwait(true);
            DiscoveredCluster[] clusters = (await response.Content.ReadFromJsonAsync<DiscoveredCluster[]>(ContractJson.Options, token).ConfigureAwait(true))!;
            return clusters.Length == 2 && clusters.All(item => item.Peers.Any(peer => peer.Status == Contracts.Cluster.EPeerStatus.Ready));
        }, timeout.Token).ConfigureAwait(true);
        using ClientWebSocket viewer = new(), controller = new();
        await ConnectAsync(viewer, client.Address, cluster + "a", timeout.Token).ConfigureAwait(true);
        await ConnectAsync(controller, client.Address, cluster + "b", timeout.Token).ConfigureAwait(true);
        JsonElement firstDescription = await ReceiveAsync(viewer, 7, timeout.Token).ConfigureAwait(true), secondDescription = await ReceiveAsync(controller, 7, timeout.Token).ConfigureAwait(true);
        Assert.True(firstDescription.GetProperty("annotations").GetBoolean());
        Assert.True(secondDescription.GetProperty("annotations").GetBoolean());
        Assert.NotEqual(firstDescription.GetProperty("peerId").GetString(), secondDescription.GetProperty("peerId").GetString());
        DesktopMonitor monitor = (await ReceiveAsync(viewer, 1, timeout.Token).ConfigureAwait(true)).Deserialize<DesktopMonitor[]>(ContractJson.Options)![0];
        await ReceiveAsync(controller, 1, timeout.Token).ConfigureAwait(true);
        await SendAsync(viewer, [11, 1], timeout.Token).ConfigureAwait(true);
        await SendAsync(viewer, [12, 1], timeout.Token).ConfigureAwait(true);
        await SendAsync(controller, [12, 1], timeout.Token).ConfigureAwait(true);
        await ReceiveAsync(viewer, 12, timeout.Token).ConfigureAwait(true); await ReceiveAsync(controller, 12, timeout.Token).ConfigureAwait(true);

        // 実ワーカーが試験専用の共有領域を開いたことも確認します。
        using MemoryMappedFile shared = MemoryMappedFile.OpenExisting(group, MemoryMappedFileRights.Read);
        Assert.False(shared.SafeMemoryMappedFileHandle.IsInvalid);
        await SendAsync(viewer, [13, 0, 80, 80, 2, 9], timeout.Token).ConfigureAwait(true);
        JsonElement remote = await UntilAsync(controller, value => value.GetProperty("points").GetArrayLength() != 0, timeout.Token).ConfigureAwait(true);
        Assert.Equal(monitor.Left + 80, remote.GetProperty("points")[0][3].GetInt32());
        Assert.Equal(monitor.Top + 80, remote.GetProperty("points")[0][4].GetInt32());
        Assert.Equal(9, remote.GetProperty("points")[0][2].GetInt32());
        Assert.Single(remote.GetProperty("cursors").EnumerateArray());
        await SendAsync(viewer, [11, 0], timeout.Token).ConfigureAwait(true);
        await UntilAsync(controller, value => value.GetProperty("cursors").GetArrayLength() == 0, timeout.Token).ConfigureAwait(true);
        viewer.Abort(); controller.Abort();
        await CorePeer.WaitAsync(async token => await first.GetAsync<int>("desktop.status", new { }, token).ConfigureAwait(true) == 0
            && await second.GetAsync<int>("desktop.status", new { }, token).ConfigureAwait(true) == 0, timeout.Token).ConfigureAwait(true);
    }

    /// <summary>ブラウザーと同じoriginと明示clusterで接続します。</summary>
    /// <returns>WebSocket接続です。</returns>
    private static async Task ConnectAsync(ClientWebSocket socket, Uri address, string cluster, CancellationToken cancellationToken = default)
    {
        socket.Options.SetRequestHeader("Origin", address.GetLeftPart(UriPartial.Authority));
        UriBuilder uri = new(address) { Scheme = "ws", Path = "/client/desktop", Query = "host=annotation-host&cluster=" + cluster };
        await socket.ConnectAsync(uri.Uri, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>映像設定も実入力も送らず、表示とモードだけを指定します。</summary>
    /// <returns>固定messageの送信です。</returns>
    private static async Task SendAsync(ClientWebSocket socket, int[] values, CancellationToken cancellationToken = default)
    {
        byte[] bytes = new byte[24];
        for (int index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(index * 4), values[index]);
        }
        await socket.SendAsync(bytes, WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>有限bufferで実際の中継headerを検証します。</summary>
    /// <returns>対象kindのJSONです。</returns>
    private static async Task<JsonElement> ReceiveAsync(ClientWebSocket socket, int expected, CancellationToken cancellationToken = default)
    {
        byte[] bytes = new byte[65536 + 24];
        while (!cancellationToken.IsCancellationRequested)
        {
            int offset = 0;
            ValueWebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(bytes.AsMemory(offset), cancellationToken).ConfigureAwait(false);
                Assert.Equal(WebSocketMessageType.Binary, result.MessageType);
                offset += result.Count;
            }
            while (!result.EndOfMessage);
            Assert.Equal(offset - 24, DesktopWire.PayloadLength(bytes.AsSpan(0, 24)));
            int kind = BinaryPrimitives.ReadInt32BigEndian(bytes);
            if (kind == 8)
            {
                Assert.Fail("Desktop worker fault: " + System.Text.Encoding.UTF8.GetString(bytes, DesktopWire.HeaderSize, offset - DesktopWire.HeaderSize));
            }
            if (kind == expected)
            {
                return JsonSerializer.Deserialize<JsonElement>(bytes.AsSpan(24, offset - 24));
            }
        }
        throw new OperationCanceledException(cancellationToken);
    }

    /// <summary>他方向の更新が届くまでsnapshotを読みます。</summary>
    /// <returns>条件を満たすpayloadです。</returns>
    private static async Task<JsonElement> UntilAsync(ClientWebSocket socket, Func<JsonElement, bool> predicate, CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            JsonElement value = await ReceiveAsync(socket, 12, cancellationToken).ConfigureAwait(false);
            if (predicate(value))
            {
                return value;
            }
        }
        throw new OperationCanceledException(cancellationToken);
    }
}
