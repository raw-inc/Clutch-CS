using System.Buffers;
using System.Security.Cryptography;

namespace ReArtWord.Contents.BackgroundService.Contracts.Serialization;

/// <summary>確定したJSON片をハッシュへ流し、出力全体を保持しない再利用バッファーです。</summary>
/// <param name="hash">寿命を呼出元が管理するハッシュです。</param>
public sealed class HashBufferWriter(IncrementalHash hash) : IBufferWriter<byte>, IDisposable
{
    private static readonly ArrayPool<byte> pool = ArrayPool<byte>.Create(16777216, 2);

    // 抑制理由: 必要容量に応じて借用配列を交換し、旧配列をpoolへ返す所有フィールドです。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "可変長IBufferWriterの借用領域を交換・返却します。")]
    private byte[] buffer = [];
    private bool disposed;

    /// <summary>書き込まれた範囲を直ちにハッシュへ反映します。</summary>
    public void Advance(int count)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, buffer.Length);
        hash.AppendData(buffer.AsSpan(0, count));
    }

    /// <summary>既存領域を優先して次のJSON片の作業範囲を返します。</summary>
    /// <returns>要求サイズ以上の再利用領域です。</returns>
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return buffer;
    }

    /// <summary>既存領域を優先して同期書込み用の作業範囲を返します。</summary>
    /// <returns>要求サイズ以上の再利用領域です。</returns>
    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return buffer;
    }

    /// <summary>上限付きpoolへ作業領域を返し、機密内容を残しません。</summary>
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
        }
    }

    /// <summary>必要なときだけ作業領域を拡張し、旧領域を速やかに返します。</summary>
    private void Ensure(int sizeHint)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        int length = Math.Max(4096, sizeHint);
        if (buffer.Length < length)
        {
            byte[] next = pool.Rent(length);
            if (buffer.Length != 0)
            {
                pool.Return(buffer, clearArray: true);
            }
            buffer = next;
        }
    }
}
