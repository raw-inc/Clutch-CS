using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.State;
using ReArtWord.Contents.BackgroundService.CoreService.Management;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop;
using ReArtWord.Contents.BackgroundService.CoreService.Files.Transfers;
using ReArtWord.Contents.BackgroundService.CoreService.SignalScope;
using ReArtWord.Contents.BackgroundService.CoreService.States;
using ReArtWord.Contents.BackgroundService.CoreService.Terminal;
using ReArtWord.Contents.BackgroundService.Contracts.Terminal;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>監視タスクが起動しない場合も、Host停止で所有セッションを回収します。</summary>
public sealed class SessionServiceLifetimeTests
{
    /// <summary>起動直後に停止しても、先に受け付けた状態接続を残しません。</summary>
    /// <returns>接続の回収と、停止後の新規受付拒否です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StateSessionIsClosedAtShutdownAsync(bool cancelStartup)
    {
        using MediaTestFiles files = new();
        ManagementTestPeer peer = new(files.Root, Guid.NewGuid().ToString("N"), "peer", 0, true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = ((IAsyncDisposable)peer).ConfigureAwait(true);
        using StateStreamService service = new(peer.Runtime.Configuration, new(new SessionTestLog(), "peer"), peer.Runtime);
        ManagementRegistry registry = new();
        service.Register(registry);
        ManagementReply opened = await registry.ExecuteAsync(Command("state-stream.open", Guid.NewGuid()));
        Assert.Equal(EOutcome.Succeeded, opened.Outcome);
        StateStreamTicket ticket = opened.Value!.Json.Deserialize<StateStreamTicket>()!;
        try
        {
            // .NET 10で起動待ちの監視が取り消される状態を、スケジューラーの速さに依存せず再現します。
            await service.StartAsync(new CancellationToken(cancelStartup));
            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            ManagementReply count = await registry.ExecuteAsync(Command("state-stream.status", null));
            Assert.Equal(0, count.Value!.Json.GetInt32());
            Assert.Equal(EOutcome.NotExecuted, (await registry.ExecuteAsync(Command("state-stream.open", Guid.NewGuid()))).Outcome);
        }
        finally
        {
            // 回帰時も試験が作った待受を不正チケットで終了させます。
            using TcpClient client = new();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, ticket.Port);
                await client.GetStream().WriteAsync(new byte[32]);
            }
            catch (SocketException)
            {
                // 正常に回収された待受には接続できません。
            }
        }
    }

    /// <summary>各セッション管理は、監視が開始されなかった停止後も新しい資源を作りません。</summary>
    /// <returns>画面・転送・端末の一貫した受付拒否です。</returns>
    [Theory]
    [InlineData("desktop")]
    [InlineData("capture")]
    [InlineData("file")]
    [InlineData("terminal")]
    public async Task StoppedServicesRejectNewSessionsAsync(string kind)
    {
        using MediaTestFiles files = new();
        ManagementTestPeer peer = new(files.Root, Guid.NewGuid().ToString("N"), "peer", 0, true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = ((IAsyncDisposable)peer).ConfigureAwait(true);
        ManagementRegistry registry = new();
        Microsoft.Extensions.Hosting.BackgroundService service;
        string operation;
        object body = Guid.NewGuid();
        switch (kind)
        {
            case "desktop":
                DesktopService desktop = new(peer.Runtime.Configuration, new(new SessionTestLog(), "peer"));
                desktop.Register(registry);
                service = desktop;
                operation = "desktop.open";
                break;
            case "capture":
                SignalScopeService capture = new(peer.Runtime.Configuration, new(new SessionTestLog(), "peer"));
                capture.Register(registry);
                service = capture;
                operation = "signal-scope.open";
                break;
            case "file":
                FileTransferStreams transfers = new(peer.Runtime.Configuration, peer.Cluster);
                transfers.Register(registry);
                service = transfers;
                operation = "transfer.stream.open";
                break;
            case "terminal":
                TerminalService terminal = new(peer.Runtime.Configuration, peer.Repository, new(new SessionTestLog(), "peer"));
                terminal.Register(registry);
                service = terminal;
                operation = "terminal.create";
                body = new TerminalRequest(Guid.NewGuid(), Guid.NewGuid());
                break;
            default:
                throw new ArgumentException("Unknown session service.", nameof(kind));
        }
        using (service)
        {
            await service.StartAsync(new CancellationToken(true));
            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            ManagementReply result = await registry.ExecuteAsync(Command(operation, body));
            Assert.Equal(EOutcome.NotExecuted, result.Outcome);
            Assert.Equal("Management.InvalidRequest", result.Reason);
        }
    }

    /// <summary>試験内の管理登録だけを呼び出します。</summary>
    /// <returns>副作用の識別子を固定した要求です。</returns>
    private static ManagementCommand Command(string operation, object? body)
        => new(Guid.NewGuid(), "fixture", "peer", Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1), operation, NodeValue.FromObject(body));
}
