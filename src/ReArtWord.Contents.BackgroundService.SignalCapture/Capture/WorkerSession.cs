using ReArtWord.Contents.BackgroundService.SignalCapture.Capture;
using ReArtWord.Contents.BackgroundService.SignalCapture.Packets;
using ReArtWord.Contents.BackgroundService.SignalCapture.Protocol;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.Capture;

/// <summary>最大16取得元の寿命・キュー・再構成と累計統計を所有します。</summary>
internal sealed class WorkerSession : IDisposable
{
    private readonly Lock sync = new();
    private readonly Lock encoding = new();
    private readonly CaptureFactory factory;
    private readonly Func<string, ICaptureSource> createSource;
    private readonly PacketQueue queue;
    private readonly PacketEncoder encoder = new();
    private readonly List<ActiveSource> sources = new();
    private EndpointFilter endpointFilter = new(null);
    private long generation;
    private long received;
    private long dropped;
    private long suppressed;
    private int accepting;

    /// <summary>履歴を持たないセッションを構成します。</summary>
    internal WorkerSession(CaptureFactory factory, PacketQueue queue, Func<string, ICaptureSource>? createSource = null)
    {
        this.factory = factory;
        this.queue = queue;
        this.createSource = createSource ?? factory.Create;
    }

    /// <summary>全指定を検証後、各取得元を独立して開始します。</summary>
    /// <returns>部分失敗も含む取得状態。</returns>
    internal PipeEvent Start(PipeCommand command)
    {
        SourceRequest[] requested = command.Sources ?? Array.Empty<SourceRequest>();
        if (requested.Length is < 1 or > 16)
        {
            throw new ArgumentException("Specify between 1 and 16 sources.");
        }
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (SourceRequest request in requested)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.AdapterId);
            string identity = request.Backend + ":" + request.AdapterId;
            if (!factory.Supports(request.Backend) || 4096 < request.AdapterId.Length || !unique.Add(identity))
            {
                throw new ArgumentException("Invalid or duplicate capture source.");
            }
        }

        var filter = new EndpointFilter(command.ExcludedTcpEndpoints, command.ExcludedEndpoints);
        using (sync.EnterScope())
        {
            if (Volatile.Read(ref accepting) != 0)
            {
                throw new ArgumentException("Stop the current capture before starting another.");
            }
            ActiveSource[] starting = requested.Select(request => new ActiveSource(request)).ToArray();
            long currentGeneration;
            using (encoding.EnterScope())
            {
                sources.Clear();
                sources.AddRange(starting);
                queue.Reset();
                encoder.Clear();
                Volatile.Write(ref endpointFilter, filter);
                received = 0;
                dropped = 0;
                suppressed = 0;
                currentGeneration = Interlocked.Increment(ref generation);
                Volatile.Write(ref accepting, 1);
            }

            // 後続 NIC の初期化を待つ間も、先行 NIC の解析・出力と状態通知を進めます。
            foreach (ActiveSource source in starting)
            {
                SourceRequest request = source.Request;
                try
                {
                    source.Capture = createSource(request.Backend);
                    CaptureAdapter? adapter = source.Capture.GetAdapters().FirstOrDefault(item => string.Equals(item.Id, request.AdapterId, StringComparison.Ordinal));
                    if (adapter is null)
                    {
                        throw new InvalidOperationException("Capture adapter not found.");
                    }
                    string sourceId = request.Backend + ":" + request.AdapterId;
                    source.Capture.Start(adapter, packet => Receive(packet, sourceId, currentGeneration));
                }
                catch (Exception exception) when (CaptureFactory.IsCaptureError(exception))
                {
                    source.Error = CaptureFactory.ErrorMessage(exception);
                    Close(source);
                }
            }

            return State();
        }
    }

    /// <summary>原本をキューへ渡す最初のマネージ境界で中継 TCP を除外します。</summary>
    private void Receive(PacketRecord packet, string sourceId, long currentGeneration)
    {
        if (Volatile.Read(ref accepting) == 0 || currentGeneration != Interlocked.Read(ref generation))
        {
            return;
        }
        Interlocked.Increment(ref received);
        if (Volatile.Read(ref endpointFilter).Excludes(packet.Data, packet.LinkType))
        {
            Interlocked.Increment(ref suppressed);
            return;
        }

        if (9000 < packet.Data.Length)
        {
            Interlocked.Increment(ref dropped);
            return;
        }

        queue.Enqueue(new QueuedPacket(currentGeneration, sourceId, packet));
    }

    /// <summary>停止した世代を出力へ持ち越さず、再構成を直列化します。</summary>
    /// <returns>有効な世代なら送信フレーム。</returns>
    internal byte[]? Encode(QueuedPacket packet)
    {
        using (encoding.EnterScope())
        {
            if (accepting == 0 || packet.Generation != generation)
            {
                if (generation <= packet.Generation + 1)
                {
                    Interlocked.Increment(ref dropped);
                }
                return null;
            }

            // 更新前に投入済みの原本も、新しい除外集合の応答後には出力しません。
            if (endpointFilter.Excludes(packet.Packet.Data, packet.Packet.LinkType))
            {
                Interlocked.Increment(ref suppressed);
                return null;
            }
            return encoder.Encode(packet);
        }
    }

    /// <summary>全件検証に成功した除外集合だけを原子的に置換します。</summary>
    /// <returns>置換完了の応答。</returns>
    internal PipeEvent ReplaceExclusions(PipeCommand command)
    {
        var replacement = new EndpointFilter(command.ExcludedTcpEndpoints, command.ExcludedEndpoints);
        using (encoding.EnterScope())
        {
            Volatile.Write(ref endpointFilter, replacement);
            return new PipeEvent("exclusions");
        }
    }

    /// <summary>全コールバック完了後に未出力原本と再構成状態を破棄します。</summary>
    /// <returns>停止後の取得状態。</returns>
    internal PipeEvent Stop()
    {
        using (sync.EnterScope())
        {
            using (encoding.EnterScope())
            {
                Volatile.Write(ref accepting, 0);
                Interlocked.Increment(ref generation);
            }
            foreach (ActiveSource source in sources)
            {
                Close(source);
            }
            using (encoding.EnterScope())
            {
                queue.Clear();
                encoder.Clear();
            }
            return State();
        }
    }

    /// <summary>バックエンドの異常終了も反映した現在状態。</summary>
    /// <returns>state 応答。</returns>
    internal PipeEvent State()
    {
        using (encoding.EnterScope())
        {
            SourceState[] states = sources.Select(source => source.State()).ToArray();
            return new PipeEvent("state", Running: states.Any(source => source.Running), Sources: states);
        }
    }

    /// <summary>意図的除外と過負荷・ネイティブ欠落を区別します。</summary>
    /// <returns>stats 応答。</returns>
    internal PipeEvent Statistics()
    {
        using (encoding.EnterScope())
        {
            long scope = sources.Sum(source => source.Capture?.Suppressed ?? 0);
            long nativeDropped = sources.Sum(source => source.Capture?.Dropped ?? 0);
            return new PipeEvent("stats", Received: Interlocked.Read(ref received) + scope, Dropped: Interlocked.Read(ref dropped) + queue.Dropped + nativeDropped, Suppressed: Interlocked.Read(ref suppressed) + scope);
        }
    }

    /// <summary>一取得元の終了失敗でも、残りの解放を継続します。</summary>
    private static void Close(ActiveSource source)
    {
        if (source.Closed)
        {
            return;
        }
        try
        {
            using (ICaptureSource? capture = source.Capture)
            {
                capture?.Stop();
            }
        }
        catch (Exception exception) when (CaptureFactory.IsCaptureError(exception))
        {
            source.Error = CaptureFactory.ErrorMessage(exception);
        }
        finally
        {
            source.Closed = true;
        }
    }

    /// <summary>全取得元を終了します。</summary>
    public void Dispose() => Stop();
}
