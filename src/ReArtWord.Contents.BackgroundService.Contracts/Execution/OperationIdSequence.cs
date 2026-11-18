using System.Buffers.Binary;

namespace ReArtWord.Contents.BackgroundService.Contracts.Execution;

/// <summary>ランダムな発行元と連番を持つUUIDv8を発行し、PCの日時を識別子へ含めません。</summary>
public sealed class OperationIdSequence
{
    private readonly Lock gate = new();
    private Guid generation;
    private uint sequence;

    /// <summary>起動前の発行元を再利用せず、新しい発行元を作ります。</summary>
    public OperationIdSequence() : this(Guid.NewGuid(), 0)
    {
    }

    /// <summary>発行順と桁あふれの境界を、同じ公開発行処理で検証できるようにします。</summary>
    internal OperationIdSequence(Guid generation, uint sequence)
    {
        this.generation = generation;
        this.sequence = sequence;
    }

    /// <summary>同じ発行元では連番を増やし、上限後は別の発行元へ切り替えます。</summary>
    /// <returns>日時補正やCoordinatorの有無に依存しない、新規操作のIDです。</returns>
    public Guid Next()
    {
        using (gate.EnterScope())
        {
            if (sequence == uint.MaxValue)
            {
                generation = Guid.NewGuid();
                sequence = 0;
            }

            // UUIDv8の先頭96ビットは、形式を示す6ビットと90ビットのランダムな発行元です。
            // 末尾32ビットに連番を格納します。連番の途中には未使用・未完了の要求があり得るため、
            // 大きい番号の確定だけを根拠に、それ以前の番号を失効させてはいけません。
            // https://www.rfc-editor.org/rfc/rfc9562.html#section-5.8
            Span<byte> bytes = stackalloc byte[16];
            _ = generation.TryWriteBytes(bytes, bigEndian: true, out _);
            bytes[6] = (byte)((bytes[6] & 15) | 128);
            bytes[8] = (byte)((bytes[8] & 63) | 128);
            BinaryPrimitives.WriteUInt32BigEndian(bytes[12..], ++sequence);
            return new Guid(bytes, bigEndian: true);
        }
    }
}
