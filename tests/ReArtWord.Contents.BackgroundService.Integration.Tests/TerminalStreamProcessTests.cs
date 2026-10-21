using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Terminal;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>実PTYの持続接続、切断後の維持、履歴境界と所有権を実通信で検証します。</summary>
public sealed class TerminalStreamProcessTests
{
    /// <summary>入力を一回適用し、再接続で履歴を読み直してもシェルを作り直しません。</summary>
    /// <returns>実CoreとClientHostを通るWebSocketの検証です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamPreservesShellAndSeparatesReplayAsync(bool explicitShell)
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer core = new(cluster, "terminal", port, true);
        ClientHostProcess client = new(Path.Combine(core.DirectoryPath, "client"));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable coreLifetime = core.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable clientLifetime = client.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
        await core.StartAsync(timeout.Token).ConfigureAwait(true);
        await client.StartAsync(timeout.Token).ConfigureAwait(true);
        using (HttpResponseMessage network = await client.SendAsync(HttpMethod.Post, "client/network", new ClientNetworkBinding(core.InterfaceId, "127.0.0.1", "127.255.255.255", port), cancellationToken: timeout.Token).ConfigureAwait(true))
        {
            network.EnsureSuccessStatusCode();
        }
        Guid instance = Guid.Empty;
        await CorePeer.WaitAsync(async token =>
        {
            using HttpResponseMessage reply = await client.SendAsync(HttpMethod.Get, "client/clusters", cancellationToken: token).ConfigureAwait(true);
            DiscoveredCluster[] clusters = (await reply.Content.ReadFromJsonAsync<DiscoveredCluster[]>(ContractJson.Options, token).ConfigureAwait(true))!;
            instance = clusters.SelectMany(item => item.Peers).FirstOrDefault()?.InstanceId ?? Guid.Empty;
            return instance != Guid.Empty;
        }, timeout.Token).ConfigureAwait(true);
        using HttpResponseMessage available = await client.SendAsync(HttpMethod.Get, "api/terminal/shells", null, cluster, "terminal", cancellationToken: timeout.Token).ConfigureAwait(true);
        available.EnsureSuccessStatusCode();
        TerminalShell[] shells = (await available.Content.ReadFromJsonAsync<TerminalShell[]>(ContractJson.Options, timeout.Token).ConfigureAwait(true))!;
        Assert.NotEmpty(shells);
        string alternative = "sh";
        if (OperatingSystem.IsWindows())
        {
            alternative = "cmd";
        }
        TerminalShell choice = shells.First(shell => string.Equals(shell.Name, alternative, StringComparison.OrdinalIgnoreCase));
        string? selectedShell = null;
        if (explicitShell)
        {
            selectedShell = choice.Path;
        }
        TerminalRequest create = await TerminalTestRequest.CreateAsync(core, selectedShell, timeout.Token).ConfigureAwait(true);
        using (HttpResponseMessage rejected = await client.SendAsync(HttpMethod.Post, "api/terminal/create", create with { SessionId = Guid.NewGuid(), Shell = Path.Combine(core.DirectoryPath, "not-a-discovered-shell") }, cluster, "terminal", Guid.NewGuid(), timeout.Token).ConfigureAwait(true))
        {
            Assert.False(rejected.IsSuccessStatusCode);
        }
        using (HttpResponseMessage response = await client.SendAsync(HttpMethod.Post, "api/terminal/create", create, cluster, "terminal", Guid.NewGuid(), timeout.Token).ConfigureAwait(true))
        {
            response.EnsureSuccessStatusCode();
            TerminalView session = (await response.Content.ReadFromJsonAsync<TerminalView>(ContractJson.Options, timeout.Token).ConfigureAwait(true))!;
            Assert.Equal(selectedShell ?? shells[0].Path, session.Shell);
            Assert.Equal(create.Elevated, session.Elevated);
        }
        string file = Path.Combine(core.DirectoryPath, "stream-result.txt");
        using (ClientWebSocket socket = await ConnectAsync(client.Address, cluster, instance, create, timeout.Token).ConfigureAwait(true))
        {
            _ = await UntilReadyAsync(socket, timeout.Token).ConfigureAwait(true);
            string command = $"printf 'once\\n' >> '{file}'\r";
            if (OperatingSystem.IsWindows())
            {
                command = $"[IO.File]::AppendAllText('{file}', 'once' + [Environment]::NewLine)\r";
                if (explicitShell)
                {
                    command = $"echo once>>\"{file}\"\r";
                }
            }
            TerminalRequest input = create with { Data = Convert.ToBase64String(Encoding.UTF8.GetBytes(command)) };
            await SendAsync(socket, new { type = "input", request = input }, timeout.Token).ConfigureAwait(true);
            bool acknowledged = false;
            while (!acknowledged)
            {
                JsonElement message = await ReceiveAsync(socket, timeout.Token).ConfigureAwait(true);
                string? type = message.GetProperty("type").GetString();
                string ack = "ack", output = "output";
                if (string.Equals(type, ack, StringComparison.Ordinal))
                {
                    Assert.Equal(1, message.GetProperty("session").GetProperty("sequence").GetInt64());
                    acknowledged = true;
                }
                else
                {
                    if (string.Equals(type, output, StringComparison.Ordinal))
                    {
                        Assert.False(message.GetProperty("history").GetBoolean());
                        await ConsumeAsync(socket, message, timeout.Token).ConfigureAwait(true);
                    }
                }
            }
            await CorePeer.WaitAsync(async token => File.Exists(file) && !string.IsNullOrEmpty(await FileObservation.ReadTextAsync(file, token).ConfigureAwait(true)), timeout.Token).ConfigureAwait(true);
        }
        Assert.Equal(["once"], await File.ReadAllLinesAsync(file, timeout.Token).ConfigureAwait(true));
        Assert.Single(await core.GetAsync<TerminalView[]>("terminal.list", timeout.Token).ConfigureAwait(true));
        TerminalRequest claim = create with { Owner = Guid.NewGuid() };
        using (HttpResponseMessage response = await client.SendAsync(HttpMethod.Post, "api/terminal/claim", claim, cluster, "terminal", Guid.NewGuid(), timeout.Token).ConfigureAwait(true))
        {
            response.EnsureSuccessStatusCode();
        }
        using (ClientWebSocket reconnected = await ConnectAsync(client.Address, cluster, instance, claim, timeout.Token).ConfigureAwait(true))
        {
            Assert.True(0 < await UntilReadyAsync(reconnected, timeout.Token).ConfigureAwait(true));
        }
        using HttpResponseMessage catalog = await client.SendAsync(HttpMethod.Get, "client/terminal/sessions", cancellationToken: timeout.Token).ConfigureAwait(true);
        TerminalPeerSessions[] rows = (await catalog.Content.ReadFromJsonAsync<TerminalPeerSessions[]>(ContractJson.Options, timeout.Token).ConfigureAwait(true))!;
        Assert.Equal(create.SessionId, Assert.Single(Assert.Single(rows).Sessions).SessionId);
        using HttpResponseMessage closed = await client.SendAsync(HttpMethod.Post, "api/terminal/close", create, cluster, "terminal", Guid.NewGuid(), timeout.Token).ConfigureAwait(true);
        closed.EnsureSuccessStatusCode();
        Assert.Empty(await core.GetAsync<TerminalView[]>("terminal.list", timeout.Token).ConfigureAwait(true));
    }

    /// <summary>同一originと固定実体を指定して接続します。</summary>
    /// <returns>呼出元が解放するWebSocketです。</returns>
    // 抑制理由: 戻り値のSocketを呼出元のusingへ移譲します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW141", Justification = "呼出元へ所有権を移譲します。")]
    private static async Task<ClientWebSocket> ConnectAsync(Uri address, string cluster, Guid instance, TerminalRequest session, CancellationToken cancellationToken = default)
    {
        ClientWebSocket socket = new();
        bool connected = false;
        try
        {
            socket.Options.SetRequestHeader("Origin", address.GetLeftPart(UriPartial.Authority));
            UriBuilder uri = new(address) { Scheme = "ws", Path = "/client/terminal", Query = $"cluster={cluster}&peer=terminal&instance={instance:D}&session={session.SessionId:D}&owner={session.Owner:D}" };
            await socket.ConnectAsync(uri.Uri, cancellationToken).ConfigureAwait(true);
            connected = true;
            return socket;
        }
        finally
        {
            if (!connected)
            {
                socket.Dispose();
            }
        }
    }

    /// <summary>初期履歴を描画済みとして確認し、ライブ出力との境界を待ちます。</summary>
    /// <returns>履歴のバイト数です。</returns>
    private static async Task<int> UntilReadyAsync(ClientWebSocket socket, CancellationToken cancellationToken = default)
    {
        int bytes = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            JsonElement message = await ReceiveAsync(socket, cancellationToken).ConfigureAwait(true);
            string ready = "ready";
            string? type = message.GetProperty("type").GetString();
            if (string.Equals(type, ready, StringComparison.Ordinal))
            {
                return bytes;
            }
            Assert.Equal("output", message.GetProperty("type").GetString());
            Assert.True(message.GetProperty("history").GetBoolean());
            bytes += Convert.FromBase64String(message.GetProperty("output").GetProperty("data").GetString()!).Length;
            await ConsumeAsync(socket, message, cancellationToken).ConfigureAwait(true);
        }
        throw new OperationCanceledException(cancellationToken);
    }

    /// <summary>一つの出力だけを描画済みとして返します。</summary>
    /// <returns>通知の送信です。</returns>
    private static Task ConsumeAsync(ClientWebSocket socket, JsonElement message, CancellationToken cancellationToken = default) =>
        SendAsync(socket, new { type = "consumed", offset = message.GetProperty("output").GetProperty("nextOffset").GetInt64() }, cancellationToken);

    /// <summary>一つの端末メッセージを送信します。</summary>
    /// <returns>送信完了です。</returns>
    private static Task SendAsync(ClientWebSocket socket, object message, CancellationToken cancellationToken = default) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message, ContractJson.Options), WebSocketMessageType.Text, true, cancellationToken);

    /// <summary>断片化されたメッセージを有限領域で読みます。</summary>
    /// <returns>一つのJSONメッセージです。</returns>
    private static async Task<JsonElement> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken = default)
    {
        byte[] buffer = new byte[128 * 1024];
        int offset = 0;
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(true);
            Assert.Equal(WebSocketMessageType.Text, result.MessageType);
            offset += result.Count;
        } while (!result.EndOfMessage);
        return JsonSerializer.Deserialize<JsonElement>(buffer.AsSpan(0, offset), ContractJson.Options);
    }
}
