using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using ReArtWord.Contents.BackgroundService.SignalCapture.Capture;
using ReArtWord.Contents.BackgroundService.SignalCapture.Packets;
using ReArtWord.Contents.BackgroundService.SignalCapture.Protocols.Osc;
using ReArtWord.Contents.BackgroundService.SignalCapture.Sending;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.Capture;

/// <summary>実通信を行わず、有限レートで既知の原本を生成します。</summary>
internal sealed class DemoCapture : ICaptureSource
{
    private readonly ManualResetEventSlim stopping = new(true);
    private readonly int packetsPerSecond;
    private Thread? running;
    private bool disposed;

    /// <summary>実通信不要の負荷試験でも、生成レートと一度の生成量を制限します。</summary>
    internal DemoCapture(int packetsPerSecond = 10)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(packetsPerSecond, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(packetsPerSecond, 100000);
        this.packetsPerSecond = packetsPerSecond;
    }

    /// <summary>生成処理の稼働状態。</summary>
    public bool IsRunning => running?.IsAlive ?? false;

    /// <summary>デモ自身の欠落はありません。</summary>
    public long Dropped => 0;

    /// <summary>デモ自身の観測点除外はありません。</summary>
    public long Suppressed => 0;

    /// <summary>デモの診断。</summary>
    public string Error => "";

    /// <summary>外部機器を列挙せず一つのデモ取得元を返します。</summary>
    /// <returns>固定 ID のデモ取得元。</returns>
    public IReadOnlyList<CaptureAdapter> GetAdapters() => new[] { new CaptureAdapter("demo0", "Demo OSC / UDP / HTTP", "No network traffic; target " + packetsPerSecond.ToString(CultureInfo.InvariantCulture) + " packets per second.") };

    /// <summary>前の生成処理を停止してから再開します。</summary>
    public void Start(CaptureAdapter adapter, Action<PacketRecord> receive)
    {
        Stop();
        ObjectDisposedException.ThrowIf(disposed, this);
        stopping.Reset();
        running = new Thread(() =>
        {
            int sequence = 0;
            long generated = 0;
            Stopwatch elapsed = Stopwatch.StartNew();
            int interval = Math.Max(1, 1000 / packetsPerSecond);
            while (!stopping.IsSet)
            {
                long due = (long)(elapsed.Elapsed.TotalSeconds * packetsPerSecond) - generated;
                if (due <= 0)
                {
                    // 高レートの模擬入力を OS の粗い待機周期による大きなバーストへ変えません。
                    if (packetsPerSecond < 1000)
                    {
                        stopping.Wait(interval);
                    }
                    else
                    {
                        Thread.Yield();
                    }
                    continue;
                }
                int count = (int)Math.Min(due, 256);
                for (int index = 0; index < count && !stopping.IsSet; index++)
                {
                    receive(Create(sequence++));
                    generated++;
                }
                Thread.Yield();
            }
        })
        {
            IsBackground = true,
            Name = "SignalScope demo"
        };
        running.Start();
    }

    /// <summary>OSC・汎用 UDP・一メッセージの TCP を交互に生成します。</summary>
    /// <returns>独立したデモ原本。</returns>
    internal static PacketRecord Create(int sequence)
    {
        byte[] data;
        if (sequence % 3 == 0)
        {
            data = PacketComposer.UdpFrame(OscCodec.Build("/demo/cue", "i " + sequence.ToString(CultureInfo.InvariantCulture)), "127.0.0.1", "127.0.0.2", 9000);
        }
        else
        {
            if (sequence % 3 == 1)
            {
                data = PacketComposer.UdpFrame("demo packet"u8.ToArray(), "127.0.0.1", "127.0.0.2", 9001);
            }
            else
            {
                byte[] payload = Encoding.ASCII.GetBytes("GET /demo HTTP/1.1\r\nHost: localhost\r\n\r\n");
                data = new byte[40 + payload.Length];
                data[0] = 0x45;
                BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2), (ushort)data.Length);
                data[8] = 64;
                data[9] = 6;
                data[12] = 127;
                data[15] = 1;
                data[16] = 127;
                data[19] = 2;
                BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(20), 45000);
                BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(22), 80);
                BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(24), (uint)sequence * 1000);
                data[32] = 0x50;
                data[33] = 0x19;
                payload.CopyTo(data, 40);
            }
        }

        return new PacketRecord { Data = data, LinkType = 101, OriginalLength = data.Length, Time = DateTimeOffset.UtcNow, ObservationPoint = "demo:demo0", Observation = "demo / no network" };
    }

    /// <summary>生成中の通知が完了するまで待機します。</summary>
    public void Stop()
    {
        if (disposed)
        {
            return;
        }
        stopping.Set();
        running?.Join();
        running = null;
    }

    /// <summary>生成処理を終了します。</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        Stop();
        disposed = true;
        stopping.Dispose();
    }
}
