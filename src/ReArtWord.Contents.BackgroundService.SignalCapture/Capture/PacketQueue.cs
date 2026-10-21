namespace ReArtWord.Contents.BackgroundService.SignalCapture.Capture;

/// <summary>古い原本を先に除外し、キャプチャコールバックを待機させない有界キューです。</summary>
internal sealed class PacketQueue
{
    private readonly Lock sync = new();
    private readonly Queue<QueuedPacket> packets = new();
    private readonly int maximumCount;
    private readonly long maximumBytes;
    private long bytes;
    private long dropped;

    /// <summary>件数と原本の合計容量を同時に制限します。</summary>
    internal PacketQueue(int maximumCount = 512, long maximumBytes = 8 * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);
        this.maximumCount = maximumCount;
        this.maximumBytes = maximumBytes;
    }

    /// <summary>容量・停止によって破棄した累計。</summary>
    internal long Dropped => Interlocked.Read(ref dropped);

    /// <summary>待機中の単一出力処理を起こします。</summary>
    internal Action? Available
    {
        get;
        set;
    }

    /// <summary>新しい原本を投入し、必要な分だけ古い原本を除外します。</summary>
    internal void Enqueue(QueuedPacket packet)
    {
        using (sync.EnterScope())
        {
            if (maximumBytes < packet.Packet.Data.Length)
            {
                Interlocked.Increment(ref dropped);
                return;
            }

            while (maximumCount <= packets.Count || maximumBytes < bytes + packet.Packet.Data.Length)
            {
                bytes -= packets.Dequeue().Packet.Data.Length;
                Interlocked.Increment(ref dropped);
            }

            packets.Enqueue(packet);
            bytes += packet.Packet.Data.Length;
        }

        Available?.Invoke();
    }

    /// <summary>出力処理へ一件だけ渡します。</summary>
    /// <returns>キューが空なら null。</returns>
    internal QueuedPacket? Read()
    {
        using (sync.EnterScope())
        {
            if (!packets.TryDequeue(out QueuedPacket? packet))
            {
                return null;
            }
            bytes -= packet.Packet.Data.Length;
            return packet;
        }
    }

    /// <summary>停止時に未出力原本を解放し、破棄数へ反映します。</summary>
    internal void Clear()
    {
        using (sync.EnterScope())
        {
            Interlocked.Add(ref dropped, packets.Count);
            packets.Clear();
            bytes = 0;
        }
    }

    /// <summary>次のセッション開始前に統計を初期化します。</summary>
    internal void Reset()
    {
        Clear();
        Interlocked.Exchange(ref dropped, 0);
    }
}
