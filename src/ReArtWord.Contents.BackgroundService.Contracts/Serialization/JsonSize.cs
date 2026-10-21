using System.Buffers;
using System.Text.Json;

namespace ReArtWord.Contents.BackgroundService.Contracts.Serialization;

/// <summary>転送・保持上限の確認で、出力全体のバイト配列を作らず同じJSON契約を計測します。</summary>
public static class JsonSize
{
    /// <summary>Serializerの検証・エスケープ・深さ制限を維持し、出力先では長さだけを数えます。</summary>
    /// <returns>指定した設定で直列化したUTF-8の正確なバイト数です。</returns>
    public static long Measure<T>(T value, JsonSerializerOptions? options = null)
    {
        JsonSerializerOptions settings = options ?? ContractJson.Options;
        using CountingWriter output = new();
        using (Utf8JsonWriter writer = new(output, new()
        {
            MaxDepth = settings.MaxDepth,
            Encoder = settings.Encoder,
            Indented = settings.WriteIndented,
            IndentCharacter = settings.IndentCharacter,
            IndentSize = settings.IndentSize,
            NewLine = settings.NewLine,
        }))
        {
            JsonSerializer.Serialize(writer, value, settings);
        }
        return output.Length;
    }

    /// <summary>Serializerの一時領域を受け取り、本文やディスク出力を保持しません。</summary>
    private sealed class CountingWriter : IBufferWriter<byte>, IDisposable
    {
        private static readonly ArrayPool<byte> pool = ArrayPool<byte>.Create(8388608, 1);

        // 抑制理由: 要求サイズに合わせて借用領域を交換し、Disposeで必ず返す所有フィールドです。
        [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "IBufferWriterの要求長とDisposeに応じて借用配列を交換・返却します。")]
        private byte[] buffer = [];
        private long length;
        private bool disposed;

        /// <summary>Serializerが確定したUTF-8の累積バイト数です。</summary>
        internal long Length => length;

        /// <summary>指定範囲を検証し、書込み量だけを加算します。</summary>
        public void Advance(int count)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(count, buffer.Length);
            length = checked(length + count);
        }

        /// <summary>前の確定範囲を再利用し、本文全体を連結しません。</summary>
        /// <returns>要求長以上の書込み領域です。</returns>
        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return buffer;
        }

        /// <summary>前の確定範囲を再利用し、同期書込み用の領域を返します。</summary>
        /// <returns>要求長以上の書込み領域です。</returns>
        public Span<byte> GetSpan(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return buffer;
        }

        /// <summary>一度に要求された範囲だけを確保し、計測済みの本文を複製しません。</summary>
        private void Ensure(int sizeHint)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
            if (buffer.Length < Math.Max(1, sizeHint))
            {
                byte[] next = pool.Rent(Math.Max(4096, sizeHint));
                if (buffer.Length != 0)
                {
                    pool.Return(buffer, clearArray: true);
                }
                buffer = next;
            }
        }

        /// <summary>本文を消去し、有限poolへ返却します。大きい一時領域は蓄積しません。</summary>
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
    }
}
