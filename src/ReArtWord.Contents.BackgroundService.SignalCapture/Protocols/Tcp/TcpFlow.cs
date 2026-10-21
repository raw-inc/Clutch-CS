using System.IO;
using ReArtWord.Contents.BackgroundService.SignalCapture.Packets;
using ReArtWord.Contents.BackgroundService.SignalCapture.Protocols.Http;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.Protocols.Tcp;

/// <summary>一方向の TCP シーケンスを有界バッファに再構成します。</summary>
// 抑制理由: bufferは外部資源を持たないMemoryStreamで、総容量を検査してフロー除外・Clear時に参照を解放します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "MemoryStreamだけを有界メモリとして使用し、フロー所有者が保持と回収を管理します。")]
internal sealed class TcpFlow
{
    private readonly MemoryStream buffer = new();
    private readonly Dictionary<uint, byte[]> pending = new();
    private uint next;
    private int pendingBytes;
    private uint? finishSequence;

    /// <summary>最初に期待するシーケンスを設定します。</summary>
    internal TcpFlow(uint sequence)
    {
        next = sequence;
        InitialSequence = sequence;
    }

    /// <summary>重複 SYN の識別に使う開始位置。</summary>
    internal uint InitialSequence
    {
        get;
    }

    /// <summary>FIN まで欠落なく受信できたか。</summary>
    internal bool Finished => finishSequence == next && pending.Count == 0;

    /// <summary>再構成用に確保した連続バッファーと欠落待ち原本の容量。</summary>
    internal long RetainedBytes => buffer.Capacity + (long)pendingBytes;

    /// <summary>再送の重複を除き、連続データを結合します。</summary>
    internal void Add(PacketRecord packet)
    {
        uint sequence = packet.Sequence + (uint)((packet.TcpFlags & 2) != 0 ? 1
                                                                            : 0);
        ReadOnlySpan<byte> data = packet.Data.AsSpan(packet.PayloadOffset, packet.PayloadLength);
        if ((packet.TcpFlags & 1) != 0)
        {
            finishSequence = unchecked(sequence + (uint)data.Length);
        }

        if (data.IsEmpty)
        {
            return;
        }

        int distance = unchecked((int)(sequence - next));
        if (0 < distance)
        {
            KeepPending(sequence, data, distance);
            return;
        }

        Append(data, distance);
        bool advanced;
        do
        {
            advanced = false;
            foreach (KeyValuePair<uint, byte[]> segment in pending)
            {
                int gap = unchecked((int)(segment.Key - next));
                if (gap <= 0)
                {
                    Append(segment.Value, gap);
                    pendingBytes -= segment.Value.Length;
                    pending.Remove(segment.Key);
                    advanced = true;
                    break;
                }
            }
        }
        while (advanced);
    }

    /// <summary>完全な HTTP メッセージを取り出します。</summary>
    /// <returns>順番に完成したメッセージ。</returns>
    internal IReadOnlyList<HttpMessage> ReadMessages(bool closed)
    {
        var messages = new List<HttpMessage>();
        while (0 < buffer.Length)
        {
            ReadOnlySpan<byte> data = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
            HttpMessage? message = HttpDecoder.Read(data, closed && pending.Count == 0, out int consumed);
            if (message is null)
            {
                break;
            }

            messages.Add(message);
            int remaining = (int)buffer.Length - consumed;
            data[consumed..].CopyTo(buffer.GetBuffer());
            buffer.SetLength(remaining);
            buffer.Position = remaining;
        }

        return messages;
    }

    /// <summary>未受信の部分だけ追記します。</summary>
    private void Append(ReadOnlySpan<byte> data, int distance)
    {
        long skip = -(long)distance;
        int incomingOffset = (int)Math.Min(data.Length, Math.Max(0, skip - buffer.Length));
        int bufferOffset = (int)Math.Max(0, buffer.Length - skip);
        int overlap = (int)Math.Min(data.Length - incomingOffset, Math.Min(skip, buffer.Length));
        if (0 < overlap && !data.Slice(incomingOffset, overlap).SequenceEqual(buffer.GetBuffer().AsSpan(bufferOffset, overlap)))
        {
            throw new FormatException("TCP retransmission conflicts with previously received data.");
        }

        if (data.Length <= skip)
        {
            return;
        }

        data = data[(int)skip..];
        if (1024 * 1024 < buffer.Length + data.Length)
        {
            throw new FormatException("TCP reassembly is limited to 1 MiB.");
        }

        buffer.Write(data);
        next = unchecked(next + (uint)data.Length);
    }

    /// <summary>順序逆転した再送の重なりを検証し、新しい末尾を保持します。</summary>
    private void KeepPending(uint sequence, ReadOnlySpan<byte> data, int distance)
    {
        pending.TryGetValue(sequence, out byte[]? previous);
        int additional = Math.Max(0, data.Length - (previous?.Length ?? 0));
        if ((previous is null && 64 <= pending.Count) || 1024 * 1024 < pendingBytes + additional || 1024 * 1024 < distance)
        {
            throw new FormatException("TCP out-of-order reassembly limit reached.");
        }

        foreach (KeyValuePair<uint, byte[]> segment in pending)
        {
            int delta = unchecked((int)(sequence - segment.Key));
            int incomingOffset = Math.Max(0, -delta);
            int existingOffset = Math.Max(0, delta);
            int overlap = Math.Min(data.Length - incomingOffset, segment.Value.Length - existingOffset);
            if (0 < overlap && !data.Slice(incomingOffset, overlap).SequenceEqual(segment.Value.AsSpan(existingOffset, overlap)))
            {
                throw new FormatException("Conflicting out-of-order TCP segments.");
            }
        }

        if (previous is null || previous.Length < data.Length)
        {
            pending[sequence] = data.ToArray();
            pendingBytes += additional;
        }
    }
}
