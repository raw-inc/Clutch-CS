namespace ReArtWord.Contents.BackgroundService.Nodes.Scripting;

/// <summary>Serializerの出力先自体に上限を設け、巨大な結果を全量蓄積しません。</summary>
internal sealed class ScriptFrameBuffer(int maximum) : MemoryStream(Math.Min(maximum, 4096))
{
    /// <summary>容量を増やす前に一フレームの上限を確認します。</summary>
    public override void Write(byte[] buffer, int offset, int count)
    {
        if (maximum < Position + count)
        {
            throw new InvalidDataException("script.outputLimit");
        }
        base.Write(buffer, offset, count);
    }

    /// <summary>容量を増やす前に一フレームの上限を確認します。</summary>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (maximum < Position + buffer.Length)
        {
            throw new InvalidDataException("script.outputLimit");
        }
        base.Write(buffer);
    }

    /// <summary>非同期Serializerにも同じ上限を適用します。</summary>
    /// <returns>メモリへの即時書込みです。</returns>
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    /// <summary>配列型の非同期書込みにも同じ上限を適用します。</summary>
    /// <returns>メモリへの即時書込みです。</returns>
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
}
