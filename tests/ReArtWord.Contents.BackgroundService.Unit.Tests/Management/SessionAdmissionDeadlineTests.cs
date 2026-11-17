using System.Net;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop;
using ReArtWord.Contents.BackgroundService.CoreService.Files.Transfers;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting;
using ReArtWord.Contents.BackgroundService.CoreService.Management;
using ReArtWord.Contents.BackgroundService.CoreService.SignalScope;
using ReArtWord.Contents.BackgroundService.CoreService.States;
using ReArtWord.Contents.BackgroundService.CoreService.Terminal;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.State;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;
using ReArtWord.Contents.BackgroundService.Tests.Shared;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>受信側の日時判定がなくても、発行元の十五秒の認証期限を越えて接続できません。</summary>
public sealed class SessionAdmissionDeadlineTests
{
    /// <summary>接続済みでもトークン送信が未完了なら、五種類すべてで認証待ちを打ち切ります。</summary>
    /// <returns>アプリ・PTY・キャプチャを起動せず、実TCPで受付終了を確認します。</returns>
    [WindowsFact]
    public async Task IssuersCloseUnauthenticatedConnectionsAtTheirOwnDeadline()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using MediaTestFiles files = new();
        SessionFailureBoundary failures = new(new SessionTestLog(), "fixture");
        using TerminalSessionTests.BufferedPty process = new([]);
        TerminalSession terminal = new(process, Guid.NewGuid(), "fixture", Guid.NewGuid(), failures);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable terminalLifetime = terminal.ConfigureAwait(true);
        TerminalConnection attachment = new("127.0.0.1", terminal, new(terminal.View.SessionId, terminal.View.Owner, Guid.NewGuid()), failures);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable attachmentLifetime = attachment.ConfigureAwait(true);
        DesktopSession desktop = new("127.0.0.1", "unused", failures, Guid.NewGuid());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable desktopLifetime = desktop.ConfigureAwait(true);
        SignalScopeSession capture = new("127.0.0.1", failures, Guid.NewGuid());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable captureLifetime = capture.ConfigureAwait(true);
        NodeStateStore store = new(new SqliteRepository(Path.Combine(files.Root, "state.db")), "fixture");
        StateStreamSession state = new("127.0.0.1", failures, Guid.NewGuid(), store, "fixture");
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable stateLifetime = state.ConfigureAwait(true);
        string loopback = "127.0.0.1";
        ClientNetworkInterface network = ObserverNetwork.GetInterfaces().Single(item => item.Addresses.Any(address => string.Equals(address.Address, loopback, StringComparison.Ordinal)));
        FileTransferSession transfer = new(new(network.InterfaceId, loopback), new("fixture", "peer", Guid.NewGuid()), new ManagementRegistry());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable transferLifetime = transfer.ConfigureAwait(true);

        // 製品の認証期限15秒に、CIやローカル負荷による観測待ちの余裕を加えます。
        // 試験の締切を伸ばして受付を許すのではなく、サーバーが接続を閉じることを待ちます。
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        await Task.WhenAll(
            WaitForRejectionAsync(attachment.Ticket.Port, attachment.Ticket.Token, timeout.Token),
            WaitForRejectionAsync(desktop.Ticket.Port, desktop.Ticket.Token, timeout.Token),
            WaitForRejectionAsync(capture.Ticket.Port, capture.Ticket.Token, timeout.Token),
            WaitForRejectionAsync(state.Ticket.Port, state.Ticket.Token, timeout.Token),
            WaitForRejectionAsync(transfer.Ticket.Port, transfer.Ticket.Token, timeout.Token)).ConfigureAwait(true);
    }

    /// <summary>正しいトークンの最後の一バイトを保留して、期限後には認証応答がないことを確認します。</summary>
    /// <returns>発行側からの接続終了です。</returns>
    private static async Task WaitForRejectionAsync(int port, string token, CancellationToken cancellationToken = default)
    {
        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken).ConfigureAwait(false);
        using NetworkStream stream = client.GetStream();
        await stream.WriteAsync(Convert.FromHexString(token).AsMemory(0, 31), cancellationToken).ConfigureAwait(false);
        Assert.Equal(0, await stream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false));
    }
}
