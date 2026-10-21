using System.Runtime.InteropServices;
using ReArtWord.Contents.BackgroundService.SignalCapture.Capture;
using ReArtWord.Contents.BackgroundService.SignalCapture.Packets;
using ReArtWord.Contents.BackgroundService.SignalCapture.Text;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.Capture;

/// <summary>デバイスを個別所有し、Npcap と libpcap の原本を NativeAOT で取得します。</summary>
internal sealed class NativePcapCaptureDangerous : ICaptureSource
{
    private readonly ManualResetEventSlim stopping = new();
    private nint handle;
    private Thread? reading;
    private long dropped;
    private int active;
    private string error = "";
    private bool disposed;

    /// <summary>受信ループの稼働状態。</summary>
    public bool IsRunning => Volatile.Read(ref active) != 0;

    /// <summary>バックエンドの欠落数。</summary>
    public long Dropped => Interlocked.Read(ref dropped);

    /// <summary>追加の観測点除外はありません。</summary>
    public long Suppressed => 0;

    /// <summary>原本を含まない診断。</summary>
    public string Error => Volatile.Read(ref error);

    /// <summary>一時一覧を必ず解放して取得元を列挙します。</summary>
    /// <returns>バックエンド固有 ID を持つ取得元。</returns>
    public IReadOnlyList<CaptureAdapter> GetAdapters()
    {
        using var message = new PcapBufferDangerous();
        if (PcapNativeDangerous.FindAll(out nint devices, message.Pointer) != 0)
        {
            throw new InvalidOperationException("libpcap adapter enumeration failed.");
        }
        try
        {
            var result = new List<CaptureAdapter>();
            var visited = new HashSet<nint>();
            for (nint item = devices; item != 0; item = Marshal.ReadIntPtr(item))
            {
                if (!visited.Add(item) || 4096 < visited.Count)
                {
                    throw new InvalidOperationException("Invalid libpcap adapter list.");
                }
                string name = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(item, nint.Size)) ?? "";
                string description = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(item, 2 * nint.Size)) ?? name;
                if (name.Length == 0 || 4096 < name.Length)
                {
                    throw new InvalidOperationException("Invalid libpcap adapter name.");
                }
                result.Add(new CaptureAdapter(name, DisplayText.Safe(description + " / " + name), "Npcap / libpcap / maximum 9000 bytes"));
            }

            return result;
        }
        finally
        {
            PcapNativeDangerous.FreeAll(devices);
        }
    }

    /// <summary>一覧とは別のハンドルを開き、複数取得元を独立して動作させます。</summary>
    public void Start(CaptureAdapter adapter, Action<PacketRecord> receive)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Stop();
        using var message = new PcapBufferDangerous();
        handle = PcapNativeDangerous.Open(adapter.Id, 9000, 1, 100, message.Pointer);
        if (handle == 0)
        {
            throw new InvalidOperationException("libpcap could not open the selected adapter.");
        }
        if (PcapNativeDangerous.SetNonBlock(handle, 1, message.Pointer) != 0)
        {
            Stop();
            throw new InvalidOperationException("libpcap does not support nonblocking capture on this adapter.");
        }

        int link = PcapNativeDangerous.LinkType(handle);
        stopping.Reset();
        error = "";
        dropped = 0;
        Volatile.Write(ref active, 1);
        reading = new Thread(() => ReadLoop(adapter.Id, link, receive)) { IsBackground = true, Name = "SignalScope pcap" };
        reading.Start();
    }

    /// <summary>借用原本をコピーした後で通知します。</summary>
    private void ReadLoop(string adapterId, int link, Action<PacketRecord> receive)
    {
        long nextStatistics = Environment.TickCount64 + 1000;
        try
        {
            while (!stopping.IsSet)
            {
                int status = PcapNativeDangerous.Next(handle, out nint header, out nint data);
                if (status < 0)
                {
                    throw new InvalidOperationException("libpcap capture ended unexpectedly.");
                }
                if (status == 0)
                {
                    stopping.Wait(5);
                }
                else
                {
                    receive(CopyPacket(header, data, link, adapterId));
                }
                if (nextStatistics <= Environment.TickCount64)
                {
                    UpdateStatistics();
                    nextStatistics = Environment.TickCount64 + 1000;
                }
            }

            UpdateStatistics();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            Volatile.Write(ref error, "libpcap capture failed.");
        }
        finally
        {
            Volatile.Write(ref active, 0);
        }
    }

    /// <summary>OS ごとの timeval 幅と原本長を検証します。</summary>
    /// <returns>借用メモリから独立した原本。</returns>
    private static PacketRecord CopyPacket(nint header, nint data, int link, string adapterId)
    {
        if (header == 0 || data == 0)
        {
            throw new InvalidOperationException("Invalid libpcap packet.");
        }
        bool windows = OperatingSystem.IsWindows();
        int timeSize = windows ? 8
                               : 16;
        int captured = Marshal.ReadInt32(header, timeSize);
        int original = Marshal.ReadInt32(header, timeSize + 4);
        if (captured is < 0 or > 9000 || original < captured)
        {
            throw new InvalidOperationException("Invalid libpcap packet length.");
        }
        long seconds = windows ? Marshal.ReadInt32(header)
                               : Marshal.ReadInt64(header);
        long microseconds = windows ? Marshal.ReadInt32(header, 4)
                                    : OperatingSystem.IsMacOS() ? Marshal.ReadInt32(header, 8)
                                                                : Marshal.ReadInt64(header, 8);
        if (microseconds is < 0 or >= 1000000)
        {
            throw new InvalidOperationException("Invalid libpcap timestamp.");
        }
        var bytes = new byte[captured];
        Marshal.Copy(data, bytes, 0, captured);
        return new PacketRecord { Data = bytes, LinkType = link, OriginalLength = original, Time = DateTimeOffset.FromUnixTimeSeconds(seconds).AddTicks(microseconds * 10), Observation = "libpcap", ObservationPoint = "pcap:" + adapterId };
    }

    /// <summary>読み取りスレッド上でだけ統計 API を呼びます。</summary>
    private void UpdateStatistics()
    {
        using var statistics = new PcapBufferDangerous();
        if (PcapNativeDangerous.Statistics(handle, statistics.Pointer) == 0)
        {
            Interlocked.Exchange(ref dropped, (uint)Marshal.ReadInt32(statistics.Pointer, 4));
        }
    }

    /// <summary>受信ループ終了後にハンドルを解放します。</summary>
    public void Stop()
    {
        if (disposed)
        {
            return;
        }
        stopping.Set();
        reading?.Join();
        reading = null;
        if (handle != 0)
        {
            PcapNativeDangerous.Close(handle);
            handle = 0;
        }

        Volatile.Write(ref active, 0);
    }

    /// <summary>停止後に待機資源を解放します。</summary>
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
