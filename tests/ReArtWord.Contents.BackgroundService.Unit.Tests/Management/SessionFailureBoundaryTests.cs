using System.Net;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>セッション単位の記録、取消、資源回収と致命的障害の区別を確認します。</summary>
public sealed class SessionFailureBoundaryTests
{
    /// <summary>列挙していなかった例外も安全な診断に変換し、例外本文を公開しません。</summary>
    /// <returns>同一セッションに結び付いた一件の診断です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordsNonfatalFailureWithoutExceptionPayload(bool unexpected)
    {
        SessionTestLog log = new();
        SessionFailureBoundary boundary = new(log, "peer");
        Guid id = Guid.NewGuid();
        Exception error = new InvalidDataException("private ticket and command");
        if (unexpected)
        {
            error = new ArithmeticException("private ticket and command");
        }
        await boundary.RunAsync(() => Task.FromException(error), "Terminal", id, "connection");
        WorkflowLogEntry entry = Assert.Single(log.Entries);
        Assert.Equal("Session.Failed", entry.Message);
        Assert.Equal(id, entry.CausationId);
        Assert.Equal(error.GetType().Name, entry.Data!.Json.GetProperty("exceptionType").GetString());
        Assert.DoesNotContain("private", entry.Data.Json.GetRawText(), StringComparison.Ordinal);
    }

    /// <summary>取消・完了・解放の複数段階が失敗しても、待受と残りの資源を回収します。</summary>
    /// <returns>後続解放、秘密値の消去、段階別診断です。</returns>
    [Fact]
    public async Task CleanupContinuesAfterCancellationAndReleaseFailures()
    {
        SessionTestLog log = new();
        SessionFailureBoundary boundary = new(log, "peer");
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using CancellationTokenSource stop = new();
        using CancellationTokenRegistration registration = stop.Token.Register(() => throw new InvalidDataException("cancel secret"));
        using CleanupResource first = new(true), second = new(false);
        byte[] secret = [1, 2, 3];
        await boundary.CloseAsync("SignalScope", Guid.NewGuid(), listener, stop, Task.FromException(new ArithmeticException("completion secret")), secret, first, second);
        Assert.True(first.Released);
        Assert.True(second.Released);
        Assert.Equal(new byte[3], secret);
        using TcpListener reclaimed = new(IPAddress.Loopback, port);
        reclaimed.Start();
        Assert.Equal(["cancel", "completion", "release"], log.Entries.Select(entry => entry.Data!.Json.GetProperty("phase").GetString()));
    }

    /// <summary>通常取消を障害記録にせず、記録先の失敗も本来処理へ再伝播しません。</summary>
    /// <returns>正常停止の無記録と、診断経路が故障しても戻れることです。</returns>
    [Fact]
    public async Task NormalCancellationAndLoggingFailureRemainIsolated()
    {
        SessionTestLog log = new();
        SessionFailureBoundary boundary = new(log, "peer");
        using CancellationTokenSource stop = new();
        await stop.CancelAsync();
        await boundary.RunAsync(() => Task.FromCanceled(stop.Token), "RemoteDesktop", Guid.NewGuid(), "connection", stop.Token);
        Assert.Empty(log.Entries);
        log.Failure = new InvalidDataException("logger secret");
        await boundary.RunAsync(() => Task.FromException(new InvalidDataException("ticket secret")), "RemoteDesktop", Guid.NewGuid(), "connection");
    }

    /// <summary>致命的例外は内部例外や集約に包まれていても回復扱いにしません。</summary>
    /// <returns>元の致命的な失敗が呼出元へ届くことです。</returns>
    // 抑制理由: 実際にメモリや実行基盤を破壊せず、致命的例外の伝播だけを検証します。
    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2201", Justification = "実障害を発生させず、例外境界が致命的な型を除外することを検証するfixtureです。")]
    public async Task FatalFailuresAreNotSwallowed()
    {
        SessionTestLog log = new();
        SessionFailureBoundary boundary = new(log, "peer");
        Exception[] errors = [new OutOfMemoryException(), new StackOverflowException(), new AccessViolationException(),
            new InvalidOperationException("wrapper", new OutOfMemoryException()), new AggregateException(new InvalidDataException(), new OutOfMemoryException())];
        foreach (Exception error in errors)
        {
            Exception? actual = await Record.ExceptionAsync(() => boundary.RunAsync(() => Task.FromException(error), "Terminal", Guid.NewGuid(), "connection"));
            Assert.Same(error, actual);
        }
        Exception? combined = await Record.ExceptionAsync(() => boundary.RunAsync(() => Task.WhenAll(
            Task.FromException(new InvalidDataException()), Task.FromException(new OutOfMemoryException())), "Terminal", Guid.NewGuid(), "connection"));
        Assert.NotNull(combined);
        Assert.Empty(log.Entries);
    }

    /// <summary>解放時の取消しだけを除外し、同時に発生した致命的障害も上位へ保持します。</summary>
    /// <returns>中継の集約例外が通常警告に変換されないことです。</returns>
    // 抑制理由: メモリ枯渇そのものを起こさず、例外の伝播を検証します。
    [Fact, System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2201", Justification = "致命的障害を隠さない境界のfixtureです。")]
    public async Task RelayCleanupPreservesConcurrentFatalFailuresAsync()
    {
        SessionTestLog log = new();
        SessionFailureBoundary boundary = new(log, "peer");
        using CancellationTokenSource stop = new();
        await stop.CancelAsync();
        await SessionRelay.DrainAsync(Task.FromCanceled(stop.Token), stop.Token);
        using Task failed = Task.WhenAll(Task.FromException(new InvalidDataException()), Task.FromException(new OutOfMemoryException()));
        await Assert.ThrowsAsync<AggregateException>(() => boundary.RunAsync(() => SessionRelay.DrainAsync(failed, stop.Token), "RemoteDesktop", Guid.NewGuid(), "connection", stop.Token));
        Assert.Empty(log.Entries);
    }

    /// <summary>資源回収中の例外を再現します。</summary>
    private sealed class CleanupResource(bool fail) : IDisposable
    {
        /// <summary>破棄の実行を確認します。</summary>
        internal bool Released
        {
            get; private set;
        }

        /// <summary>最初の破棄だけで失敗し、後続回収の検証を可能にします。</summary>
        public void Dispose()
        {
            bool first = !Released;
            Released = true;
            if (first && fail)
            {
                throw new InvalidDataException("release secret");
            }
        }
    }
}
