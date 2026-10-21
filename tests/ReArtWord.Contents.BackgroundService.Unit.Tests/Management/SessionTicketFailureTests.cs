using System.Net;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop;
using ReArtWord.Contents.BackgroundService.CoreService.SignalScope;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>不正チケットを実TCPで拒否し、後始末と診断をセッション内で完了します。</summary>
public sealed class SessionTicketFailureTests
{
    /// <summary>不正チケットでworkerを起動せず、RemoteDesktopの回収タスクを失敗させません。</summary>
    /// <returns>一接続の拒否と安全な回収です。</returns>
    [Fact]
    public async Task DesktopTicketFailureDoesNotEscapeSession()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        SessionTestLog log = new();
        DesktopSession session = new("127.0.0.1", "unused", new(log, "test"), Guid.NewGuid());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable owned = session.ConfigureAwait(true);
        await RejectAsync(session.Ticket.Port, session.Completion, log);
    }

    /// <summary>不正チケットでキャプチャを開始せず、SignalScopeの回収タスクを失敗させません。</summary>
    /// <returns>一接続の拒否と安全な回収です。</returns>
    [Fact]
    public async Task CaptureTicketFailureDoesNotEscapeSession()
    {
        SessionTestLog log = new();
        SignalScopeSession session = new("127.0.0.1", new(log, "test"), Guid.NewGuid());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable owned = session.ConfigureAwait(true);
        await RejectAsync(session.Ticket.Port, session.Completion, log);
    }

    /// <summary>許可値が一致しない接続を閉じ、完了タスクと診断を照合します。</summary>
    /// <returns>拒否後の正常な回収完了です。</returns>
    private static async Task RejectAsync(int port, Task completion, SessionTestLog log, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token).ConfigureAwait(false);
        using NetworkStream stream = client.GetStream();
        await stream.WriteAsync(new byte[32], timeout.Token).ConfigureAwait(false);
        Assert.Equal(0, await stream.ReadAsync(new byte[1], timeout.Token).ConfigureAwait(false));
        await completion.WaitAsync(timeout.Token).ConfigureAwait(false);
        Assert.Equal("InvalidDataException", Assert.Single(log.Entries).Data!.Json.GetProperty("exceptionType").GetString());
    }
}
