namespace ReArtWord.Contents.BackgroundService.ClientHost.Logs;

/// <summary>ZIPの同期終了片だけを有限バッファへ受け、HTTPには取消可能な非同期書込みだけを渡します。</summary>
internal sealed class LogExportStream(Stream output, CancellationToken lifetime) : Stream
{
    // ZIPエントリのDisposeAsyncから同期Disposeへ戻る実装でも、HTTPの同期I/Oを有効にしません。
    private readonly byte[] pending = new byte[65536];
    private int pendingCount;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>同期の終了片は64KiB内で保留し、次の非同期送信で先に排出します。</summary>
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    /// <summary>同期の終了片で全体を蓄積せず、上限超過時は出力を失敗させます。</summary>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        lifetime.ThrowIfCancellationRequested();
        if (pending.Length - pendingCount < buffer.Length)
        {
            throw new IOException("The ZIP synchronous trailer exceeded its bounded buffer.");
        }
        buffer.CopyTo(pending.AsSpan(pendingCount));
        pendingCount += buffer.Length;
    }

    /// <summary>ZIPが省略した取消トークンもHTTP応答の寿命へ接続します。</summary>
    /// <returns>下流が受け取るまでの待機です。</returns>
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await DrainAsync(lifetime).ConfigureAwait(false);
        await output.WriteAsync(buffer, lifetime).ConfigureAwait(false);
    }

    /// <summary>旧ストリームAPIも同じ取消境界を使用します。</summary>
    /// <returns>下流へ一片を書き終えるまでの待機です。</returns>
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <summary>非同期送信の待機にも取消を適用します。</summary>
    /// <returns>下流の送信完了です。</returns>
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await DrainAsync(lifetime).ConfigureAwait(false);
        await output.FlushAsync(lifetime).ConfigureAwait(false);
    }

    /// <summary>同期終了処理ではHTTPへ触れず、明示した非同期Flushまで保留します。</summary>
    public override void Flush()
    {
        lifetime.ThrowIfCancellationRequested();
    }

    /// <summary>保留した終了片を送信し、次の本文との順序を維持します。</summary>
    /// <returns>有限バッファの送信完了です。</returns>
    private async ValueTask DrainAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (pendingCount != 0)
        {
            await output.WriteAsync(pending.AsMemory(0, pendingCount), cancellationToken).ConfigureAwait(false);
            pendingCount = 0;
        }
    }

    /// <summary>送信専用なので読取りを拒否します。</summary>
    /// <returns>この操作は常に例外となります。</returns>
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>送信済みの内容へ戻る操作を拒否します。</summary>
    /// <returns>この操作は常に例外となります。</returns>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <summary>送信済みの長さを変更できません。</summary>
    public override void SetLength(long value) => throw new NotSupportedException();
}
