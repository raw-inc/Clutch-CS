using Porta.Pty;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Terminal;
using ReArtWord.Contents.BackgroundService.CoreService.Terminal;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>連続出力でメモリーを増やさず、失った位置を明示する端末境界を確認します。</summary>
public sealed class TerminalSessionTests
{
    /// <summary>画面が読まなくても出力リングは256KiB、返信は16KiBで止まります。</summary>
    /// <returns>出力読取りと所有資源の回収です。</returns>
    [Fact]
    public async Task OutputHasBoundedRetentionAndReportsDroppedPrefix()
    {
        byte[] bytes = Enumerable.Range(0, 1024 * 1024).Select(index => (byte)(index % 251)).ToArray();
        using BufferedPty process = new(bytes);
        TerminalSession session = new(process, Guid.NewGuid(), "fixture", Guid.NewGuid(), new(new SessionTestLog(), "test"));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = session.ConfigureAwait(true);
        TerminalOutput output = session.Read(0);
        Assert.Equal(bytes.Length - 256 * 1024, output.Offset);
        Assert.Equal(16384, Convert.FromBase64String(output.Data).Length);
        Assert.Equal(bytes.AsSpan((int)output.Offset, 16384).ToArray(), Convert.FromBase64String(output.Data));
        Assert.True(output.Session.Exited);
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Read(-1));
    }

    /// <summary>確定した出力を返すPTY境界です。</summary>
    private sealed class BufferedPty(byte[] output) : IPtyConnection
    {
        /// <summary>完了済みfixtureではイベント購読による資源を保持しません。</summary>
        public event EventHandler<PtyExitedEventArgs>? ProcessExited
        {
            add
            {
            }
            remove
            {
            }
        }

        /// <summary>有限の出力です。</summary>
        public Stream ReaderStream
        {
            get;
        } = new MemoryStream(output);

        /// <summary>破棄される入力先です。</summary>
        public Stream WriterStream
        {
            get;
        } = new MemoryStream();

        /// <summary>実プロセスは作成しません。</summary>
        public int Pid => 0;

        /// <summary>正常終了を返します。</summary>
        public int ExitCode => 0;

        /// <summary>既に終了したfixtureです。</summary>
        /// <returns>常に終了済みです。</returns>
        public bool WaitForExit(int milliseconds) => true;

        /// <summary>所有するプロセスはありません。</summary>
        public void Kill()
        {
        }

        /// <summary>固定出力の寸法は変更しません。</summary>
        public void Resize(int cols, int rows)
        {
        }

        /// <summary>所有ストリームを解放します。</summary>
        public void Dispose()
        {
            ReaderStream.Dispose();
            WriterStream.Dispose();
        }
    }
}
