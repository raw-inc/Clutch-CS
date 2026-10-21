using System.Buffers;

namespace ReArtWord.Contents.BackgroundService.Contracts.Serialization;

/// <summary>所有コピーの直列化中だけ本文を借用し、小さい領域だけを有限poolへ返します。</summary>
internal sealed class JsonBuffer : IBufferWriter<byte>, IDisposable
{
    private static readonly ArrayPool<byte> pool = ArrayPool<byte>.Create(262144, 2);

    // 抑制理由: 可変長の借用領域を拡張し、旧領域を必ず返す所有フィールドです。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "IBufferWriterの成長とDisposeで借用配列を交換・返却します。")]
    private byte[] buffer = [];
    private int written;
    private bool disposed;

    /// <summary>返却前に同期的に読み取る、今回書いた範囲です。</summary>
    internal ReadOnlySpan<byte> Written => buffer.AsSpan(0, written);

    /// <summary>Serializerが確定した範囲だけを記録します。</summary>
    public void Advance(int count)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, buffer.Length - written);
        written += count;
    }

    /// <summary>前の本文を保持したまま、要求された未使用領域を取得します。</summary>
    /// <returns>要求長以上の借用領域です。</returns>
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return buffer.AsMemory(written);
    }

    /// <summary>前の本文を保持したまま、同期書込み用の領域を取得します。</summary>
    /// <returns>要求長以上の借用領域です。</returns>
    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return buffer.AsSpan(written);
    }

    /// <summary>必要な場合だけ拡張し、旧領域の内容を消して返却します。</summary>
    private void Ensure(int sizeHint)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        int needed = checked(written + Math.Max(sizeHint, 1));
        if (buffer.Length < needed)
        {
            int capacity = Math.Max(needed, (int)Math.Min(Array.MaxLength, Math.Max(4096L, 2L * buffer.Length)));
            byte[] next = pool.Rent(capacity);
            Written.CopyTo(next);
            if (buffer.Length != 0)
            {
                pool.Return(buffer, clearArray: true);
            }
            buffer = next;
        }
    }

    /// <summary>本文を消去し、上限を超える大きな配列はpoolへ蓄積しません。</summary>
    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            if (buffer.Length != 0)
            {
                pool.Return(buffer, clearArray: true);
                buffer = [];
            }
            written = 0;
        }
    }
}
