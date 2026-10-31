using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace ReArtWord.Contents.BackgroundService.Runtime.Processes.Commands;

/// <summary>一人だけが読む標準pipeで、到着済みbyteだけを取り出し、空のpipeをThreadPool上で待ちません。</summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsProcessPipe : Stream
{
    private readonly FileStream source;
    private readonly CancellationToken drainDeadline;
    private int? remainingAtDeadline;

    /// <summary>元readerでは読まない同じhandleを引き受け、先読みを無効にしてOSの待機byte数と揃えます。</summary>
    internal WindowsProcessPipe(SafeFileHandle handle, CancellationToken drainDeadline)
    {
        source = new FileStream(handle, FileAccess.Read, bufferSize: 1, isAsync: false);
        this.drainDeadline = drainDeadline;
    }

    /// <summary>プロセス終了後の読取り期限に達したことを確認したかです。</summary>
    internal bool DrainDeadlineReached
    {
        get; private set;
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>空のpipeでは取消可能な待機へ戻し、到着済みbyteだけを取得します。</summary>
    /// <returns>取得byte数、書込み側が終了していれば0です。</returns>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (buffer.IsEmpty)
        {
            return 0;
        }
        while (!cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool expired = drainDeadline.IsCancellationRequested;
            DrainDeadlineReached |= expired;
            if (remainingAtDeadline == 0)
            {
                return 0;
            }
            if (!PeekNamedPipe(source.SafeFileHandle, IntPtr.Zero, 0, IntPtr.Zero, out int available, IntPtr.Zero))
            {
                int error = Marshal.GetLastPInvokeError();
                if (error is 109 or 233)
                {
                    return 0;
                }
                throw new IOException("標準出力pipeの状態を確認できません。", new Win32Exception(error));
            }
            if (expired && remainingAtDeadline is null)
            {
                // 読取りの再開が遅れても、届いている出力は捨てません。以後に追加される分は待ちません。
                remainingAtDeadline = available;
            }
            if (remainingAtDeadline is int remaining)
            {
                available = Math.Min(available, remaining);
            }
            if (0 < available)
            {
                // 他のreaderや保留Readは存在しません。先読みなしで確認済みbyte数以内だけを読みます。
                int read = source.Read(buffer.Span[..Math.Min(buffer.Length, available)]);
                if (remainingAtDeadline is not null)
                {
                    remainingAtDeadline -= read;
                }
                return read;
            }
            if (expired)
            {
                return 0;
            }
            try
            {
                await Task.Delay(2, drainDeadline).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (drainDeadline.IsCancellationRequested)
            {
                // 期限切れを確認してから、パイプに残っている分だけ読み取ります。
            }
        }
        throw new OperationCanceledException(cancellationToken);
    }

    /// <summary>配列からの非同期読取りも同じ到着確認を使用します。</summary>
    /// <returns>取得byte数です。</returns>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <summary>待機を含む同期読取りは提供しません。</summary>
    /// <returns>同期読取りは未対応です。</returns>
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>読取り専用pipeのflushは提供しません。</summary>
    public override void Flush() => throw new NotSupportedException();

    /// <summary>pipe内の位置変更は提供しません。</summary>
    /// <returns>位置変更は未対応です。</returns>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <summary>pipeの長さ変更は提供しません。</summary>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>読取り側からの書込みは提供しません。</summary>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>全読取りの終了後に、引き受けたhandleを解放します。</summary>
    // 抑制理由: Streamの標準Dispose境界として、所有するFileStreamをdisposing時だけ解放します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW140", Justification = "所有StreamのDispose実装です。通常の実行中には解放しません。")]
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            source.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>同期readを開始せず、専有している匿名pipeの到着済みbyte数を取得します。</summary>
    /// <returns>pipeの観測が成功したかです。</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekNamedPipe(SafeFileHandle pipe, IntPtr buffer, int size, IntPtr read, out int available, IntPtr remaining);
}
