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

/// <summary>ClientHostで日時を比較しなくても、Core側が認証未完了の接続を15秒で打ち切ることを検証します。</summary>
public sealed class SessionAdmissionDeadlineTests
{
    /// <summary>5種類の接続経路で、TCP接続後もトークンの受信完了までは認証期限が適用されることを確認します。</summary>
    /// <returns>実際のTCP接続を使った検証を表すタスクです。</returns>
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

        // Coreの認証タイマーは15秒です。テスト側は、負荷で切断通知の受信が遅れた場合に備えて30秒まで待ちます。
        // トークンを最後まで送らず、Coreが接続を閉じたことをReadAsyncの戻り値で確認します。
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        await Task.WhenAll(
            WaitForRejectionAsync(attachment.Ticket.Port, attachment.Ticket.Token, timeout.Token),
            WaitForRejectionAsync(desktop.Ticket.Port, desktop.Ticket.Token, timeout.Token),
            WaitForRejectionAsync(capture.Ticket.Port, capture.Ticket.Token, timeout.Token),
            WaitForRejectionAsync(state.Ticket.Port, state.Ticket.Token, timeout.Token),
            WaitForRejectionAsync(transfer.Ticket.Port, transfer.Ticket.Token, timeout.Token)).ConfigureAwait(true);
    }

    /// <summary>トークンの最後の1バイトを送らずに待ち、Coreから接続が閉じられることを確認します。</summary>
    /// <returns>Coreによる切断の確認を表すタスクです。</returns>
    private static async Task WaitForRejectionAsync(int port, string token, CancellationToken cancellationToken = default)
    {
        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken).ConfigureAwait(false);
        using NetworkStream stream = client.GetStream();
        await stream.WriteAsync(Convert.FromHexString(token).AsMemory(0, 31), cancellationToken).ConfigureAwait(false);
        Assert.Equal(0, await stream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false));
    }
}
