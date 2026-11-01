using System.IO.Pipelines;
using Porta.Pty;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.CoreService.Terminal;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>出力通知と実際の寸法変更だけがPTYへ伝わることを確認します。</summary>
public sealed class TerminalNotificationTests
{
    /// <summary>プロセス停止の失敗が資源解放や出力タスクの回収を妨げません。</summary>
    /// <returns>失敗の記録と残る資源の解放です。</returns>
    [Fact]
    public async Task KillFailureDoesNotSkipProcessDisposal()
    {
        using FixturePty process = new() { FailOnKill = true };
        SessionTestLog log = new();
        TerminalSession session = new(process, Guid.NewGuid(), "fixture", Guid.NewGuid(), new(log, "test"));
        await session.DisposeAsync();
        Assert.True(process.Released);
        Assert.Equal("kill", Assert.Single(log.Entries).Data!.Json.GetProperty("phase").GetString());
    }

    /// <summary>無変化で待機し、出力・所有権変更で起き、同じ寸法を繰り返し適用しません。</summary>
    /// <returns>非同期出力と入力の検証です。</returns>
    [Fact]
    public async Task SignalsChangesAndResizesOnlyWhenDimensionsChangeAsync()
    {
        using FixturePty process = new();
        Guid id = Guid.NewGuid(), owner = Guid.NewGuid();
        TerminalSession session = new(process, id, "fixture", owner, new(new SessionTestLog(), "test"));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = session.ConfigureAwait(true);
        using CancellationTokenSource cancelled = new();
        Task waiting = session.WaitAsync(0, owner, cancelled.Token);
        Task ownership = Task.CompletedTask, idle = Task.CompletedTask;
        try
        {
            Assert.False(waiting.IsCompleted);
            await process.Output.WriteAsync(new byte[] { 65 }).ConfigureAwait(true);
            await waiting.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(true);
            Assert.Equal("QQ==", session.Read(0).Data);
            _ = await session.InputAsync(new(id, owner, 0, Data: "YQ==")).ConfigureAwait(true);
            _ = await session.InputAsync(new(id, owner, 1, Data: "Yg==")).ConfigureAwait(true);
            Assert.Equal(0, process.Resizes);
            _ = await session.InputAsync(new(id, owner, 2, Columns: 120, Rows: 40)).ConfigureAwait(true);
            _ = await session.InputAsync(new(id, owner, 3, Columns: 120, Rows: 40)).ConfigureAwait(true);
            Assert.Equal(1, process.Resizes);
            Assert.Equal(2, process.WriterStream.Length);
            ownership = session.WaitAsync(1, owner, cancelled.Token);
            Assert.False(ownership.IsCompleted);
            _ = await session.ClaimAsync(Guid.NewGuid()).ConfigureAwait(true);
            await ownership.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(true);
            idle = session.WaitAsync(1, session.View.Owner, cancelled.Token);
            await cancelled.CancelAsync().ConfigureAwait(true);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => idle).ConfigureAwait(true);
        }
        finally
        {
            await cancelled.CancelAsync().ConfigureAwait(true);
            await Task.WhenAll(waiting, ownership, idle).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    /// <summary>実シェルを起動しない対話ストリームです。</summary>
    private sealed class FixturePty : IPtyConnection
    {
        private readonly Pipe output = new();

        /// <summary>停止時の非致命的例外を発生させます。</summary>
        internal bool FailOnKill
        {
            get; init;
        }

        /// <summary>停止失敗後も所有資源が破棄されたことを確認します。</summary>
        internal bool Released
        {
            get; private set;
        }

        /// <summary>読取方向の所有Streamを初期化します。</summary>
        internal FixturePty()
        {
            ReaderStream = output.Reader.AsStream();
        }

        /// <summary>試験から出力を追加します。</summary>
        internal PipeWriter Output => output.Writer;

        /// <summary>実際に適用された寸法変更回数です。</summary>
        internal int Resizes
        {
            get; private set;
        }

        /// <summary>fixtureではイベントを発火しません。</summary>
        public event EventHandler<PtyExitedEventArgs>? ProcessExited
        {
            add
            {
            }
            remove
            {
            }

        }

        /// <summary>保留可能な出力です。</summary>
        public Stream ReaderStream
        {
            get;
        }

        /// <summary>受け取った入力です。</summary>
        public Stream WriterStream
        {
            get;
        }
 = new MemoryStream();

        /// <summary>実プロセスは所有しません。</summary>
        public int Pid => 0;

        /// <summary>正常終了値です。</summary>
        public int ExitCode => 0;

        /// <summary>実プロセスの終了待ちは不要です。</summary>
        /// <returns>常に終了済みです。</returns>
        public bool WaitForExit(int milliseconds) => true;

        /// <summary>停止対象の実プロセスはありません。</summary>
        public void Kill()
        {
            if (FailOnKill)
            {
                throw new ArithmeticException("Fixture process stop failed.");
            }
        }

        /// <summary>寸法の通知回数を記録します。</summary>
        public void Resize(int cols, int rows)
        {
            Resizes++;
        }

        /// <summary>所有ストリームを解放します。</summary>
        public void Dispose()
        {
            if (Released)
            {
                return;
            }
            Released = true;
            ReaderStream.Dispose(); WriterStream.Dispose(); output.Writer.Complete();
        }

    }

}

