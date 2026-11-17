using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReArtWord.Contents.BackgroundService.ClientHost.Desktop;
using ReArtWord.Contents.BackgroundService.ClientHost.Networking;
using ReArtWord.Contents.BackgroundService.ClientHost.SignalScope;
using ReArtWord.Contents.BackgroundService.ClientHost.States;
using ReArtWord.Contents.BackgroundService.ClientHost.Terminal;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Requests;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Management;

/// <summary>CoreとClientHostの時計がずれていても、有効なチケットで接続できることを検証します。</summary>
public sealed class SessionTicketClockTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "clutch-ticket-clock-" + Guid.NewGuid().ToString("N"));

    /// <summary>5種類の接続経路で、チケットに記載されたトークンが発行元へ届くことを確認します。</summary>
    /// <returns>発行元の時計を前後1時間ずらした場合の検証を表すタスクです。</returns>
    // 抑制理由: 認証タスクはfinallyで取消し後に完了を待ち、listenerより先に回収します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2025", Justification = "finallyで認証タスクを必ずawaitしてから通信資源を破棄します。")]
    [Theory]
    [InlineData("desktop", -3600)]
    [InlineData("desktop", 3600)]
    [InlineData("terminal", -3600)]
    [InlineData("terminal", 3600)]
    [InlineData("state-stream", -3600)]
    [InlineData("state-stream", 3600)]
    [InlineData("signal-scope", -3600)]
    [InlineData("signal-scope", 3600)]
    [InlineData("transfer.stream", -3600)]
    [InlineData("transfer.stream", 3600)]
    public async Task IssuerClockDoesNotRejectAValidConnection(string kind, int offsetSeconds)
    {
        Directory.CreateDirectory(directory);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start(1);
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        byte[] secret = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        using UdpClient probe = new(new IPEndPoint(IPAddress.Loopback, 0));
        int udpPort = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        probe.Close();
        ClusterOptions options = new(Guid.NewGuid().ToString("N"), "peer", "host", "127.0.0.1", "127.255.255.255", udpPort, 1, true, directory)
        { HeartbeatInterval = TimeSpan.FromMilliseconds(50), PeerTimeout = TimeSpan.FromSeconds(2), RequestTimeout = TimeSpan.FromSeconds(2) };
        PeerCluster peer = new(options, new SqliteRepository(Path.Combine(directory, "state.db"))) { ReadinessCheck = _ => ValueTask.FromResult(true) };
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable peerLifetime = peer.ConfigureAwait(true);
        string terminal = "terminal";
        string transferKind = "transfer.stream";
        string loopback = "127.0.0.1";
        string operation = kind + ".open";
        if (string.Equals(kind, terminal, StringComparison.Ordinal))
        {
            operation = "terminal.attach";
        }
        Task<ManagementReply> HandleAsync(ManagementCommand command, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            NodeValue value = NodeValue.FromObject(true);
            if (string.Equals(command.Operation, operation, StringComparison.Ordinal))
            {
                value = NodeValue.FromObject(new { port, token = Convert.ToHexString(secret), expiresAt = DateTimeOffset.UtcNow.AddSeconds(offsetSeconds + 15) });
            }
            else if (!string.Equals(command.Operation, transferKind, StringComparison.Ordinal))
            {
                // この模擬Coreは上記の操作だけに対応します。未知の機能への照会は未対応と返し、実装していない通信方式が選ばれるのを防ぎます。
                return Task.FromResult(new ManagementReply(EOutcome.NotExecuted, Reason: "Management.OperationMissing"));
            }
            return Task.FromResult(new ManagementReply(EOutcome.Succeeded, value));
        }
        _ = new PeerManagementEndpoint(peer, HandleAsync, name => !string.Equals(name, operation, StringComparison.Ordinal), name => string.Equals(name, operation, StringComparison.Ordinal));
        await peer.StartAsync(timeout.Token);
        ObserverConnection connection = new(Path.Combine(directory, "observer"));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable observers = ((IAsyncDisposable)connection).ConfigureAwait(true);
        ClientNetworkInterface network = ObserverNetwork.GetInterfaces().Single(item => item.Addresses.Any(address => string.Equals(address.Address, loopback, StringComparison.Ordinal)));
        await connection.SelectAsync(new(network.InterfaceId, "127.0.0.1", "127.255.255.255", udpPort), timeout.Token);
        while (!connection.GetClusters().SelectMany(cluster => cluster.Peers).Any(item => item.InstanceId == peer.LocalInstanceId && item.Status == EPeerStatus.Ready))
        {
            await Task.Delay(20, timeout.Token);
        }
        PeerManagementClient remote = new(connection.FindObserver);
        TaskCompletionSource<byte[]> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task serving = AuthenticateAsync(listener, received, string.Equals(kind, transferKind, StringComparison.Ordinal), timeout.Token);
        try
        {
            if (string.Equals(kind, transferKind, StringComparison.Ordinal))
            {
                using ManagementTransport.Files.FileTransferClient transfer = await remote.OpenFileTransferAsync(new(peer.LocalClusterId, peer.LocalPeerId, peer.LocalInstanceId), timeout.Token);
                Assert.Equal(FileTransferLimits.StreamBytes, transfer.ChunkBytes);
            }
            else
            {
                await ConnectBrowserAsync(kind, peer, connection, remote, received.Task, timeout.Token);
            }
            Assert.Equal(secret, await received.Task.WaitAsync(timeout.Token));
        }
        finally
        {
            await timeout.CancelAsync();
            await serving.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    /// <summary>Coreの認証処理を模擬し、ClientHostから送られた32バイトのトークンを記録します。</summary>
    /// <returns>トークンをreceivedへ渡し、接続終了まで待つタスクです。</returns>
    private static async Task AuthenticateAsync(TcpListener listener, TaskCompletionSource<byte[]> received, bool acknowledge, CancellationToken cancellationToken = default)
    {
        using TcpClient accepted = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        using NetworkStream stream = accepted.GetStream();
        byte[] supplied = new byte[32];
        await stream.ReadExactlyAsync(supplied, cancellationToken).ConfigureAwait(false);
        if (acknowledge)
        {
            await stream.WriteAsync(new byte[] { 1 }, cancellationToken).ConfigureAwait(false);
        }
        received.TrySetResult(supplied);
        byte[] buffer = new byte[1024];
        while (await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) != 0)
        {
        }
    }

    /// <summary>実際のClientHostのルートへWebSocketで接続し、Coreへのトークン送信を待ちます。</summary>
    /// <returns>接続の検証と後片付けを表すタスクです。</returns>
    private static async Task ConnectBrowserAsync(string kind, PeerCluster peer, ObserverConnection connection, PeerManagementClient remote, Task<byte[]> authenticated, CancellationToken cancellationToken = default)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(connection);
        builder.Services.AddSingleton(remote);
        builder.Services.AddSingleton<TerminalCatalog>();
        WebApplication app = builder.Build();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = app.ConfigureAwait(false);
        app.Urls.Add("http://127.0.0.1:0");
        app.UseWebSockets();
        switch (kind)
        {
            case "desktop": DesktopRoutes.Map(app); break;
            case "terminal": TerminalRoutes.Map(app); break;
            case "state-stream": StateStreamRoutes.Map(app); break;
            case "signal-scope": SignalScopeRoutes.Map(app); break;
        }
        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        using ClientWebSocket socket = new();
        string address = app.Urls.Single();
        socket.Options.SetRequestHeader("Origin", address);
        string query = $"?cluster={peer.LocalClusterId}&peer={peer.LocalPeerId}&instance={peer.LocalInstanceId:D}&host=host&session={Guid.NewGuid():D}&owner={Guid.NewGuid():D}";
        try
        {
            await socket.ConnectAsync(new Uri(address.Replace("http:", "ws:", StringComparison.Ordinal) + "/client/" + kind + query), cancellationToken).ConfigureAwait(false);
            await authenticated.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            socket.Abort();
            await app.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>試験用のデータディレクトリを削除します。</summary>
    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
