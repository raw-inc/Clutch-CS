using System.Runtime.InteropServices;
using ReArtWord.Contents.BackgroundService.SignalCapture.Packets;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.Capture.PacketMonitor;

/// <summary>追加インストール不要の Packet Monitor ストリームを所有します。</summary>
internal sealed class PktMonCaptureDangerous : ICaptureSource
{
    private readonly PktMonNativeDangerous.DataCallback dataCallback;
    private readonly PktMonNativeDangerous.EventCallback eventCallback;
    private nint handle;
    private nint session;
    private nint stream;
    private Action<PacketRecord>? receive;
    private PktMonSourceFilter? sourceFilter;
    private long dropped;
    private long suppressed;
    private string error = "";
    private bool disposed;
    private int active;

    /// <summary>関数ポインターの元デリゲートを全受信期間保持します。</summary>
    internal PktMonCaptureDangerous()
    {
        dataCallback = OnDataDangerous;
        eventCallback = OnEventDangerous;
        PktMonNativeDangerous.Check(PktMonNativeDangerous.Initialize(0x10000, 0, out handle));
    }

    /// <summary>ストリームが報告した欠落数。</summary>
    public long Dropped => Interlocked.Read(ref dropped);

    /// <summary>観測点の照合によって取得対象から除外した通知数。</summary>
    public long Suppressed => Interlocked.Read(ref suppressed);

    /// <summary>最新のストリームエラー。</summary>
    public string Error => Volatile.Read(ref error);

    /// <summary>ストリーム通知に基づく取得状態。</summary>
    public bool IsRunning => Volatile.Read(ref active) != 0;

    /// <summary>物理・仮想 NIC を列挙します。</summary>
    /// <returns>キャプチャ対象。</returns>
    public IReadOnlyList<CaptureAdapter> GetAdapters()
    {
        using var sources = new PktMonSourcesDangerous(handle);
        return sources.Adapters.ToArray();
    }

    /// <summary>選択対象専用のセッションを生成し有効化します。</summary>
    public void Start(CaptureAdapter adapter, Action<PacketRecord> receive)
    {
        Stop();
        this.receive = receive;
        error = "";
        dropped = 0;
        suppressed = 0;
        bool started = false;
        try
        {
            PktMonNativeDangerous.Check(PktMonNativeDangerous.CreateSession(handle, "SignalScope-" + Guid.NewGuid().ToString("N"), out session));
            using var sources = new PktMonSourcesDangerous(handle);
            (nint pointer, PktMonSourceIdentity identity) = sources.Find(adapter.Id);
            using var allSources = new PktMonSourcesDangerous(handle, true);
            sourceFilter = new PktMonSourceFilter(identity, allSources.Identities);
            PktMonNativeDangerous.Check(PktMonNativeDangerous.AddSource(session, pointer));
            var configuration = new StreamConfigurationDangerous
            {
                Data = Marshal.GetFunctionPointerForDelegate(dataCallback),
                Event = Marshal.GetFunctionPointerForDelegate(eventCallback),
                BufferMultiplier = 10,
                SnapLength = 9000,
            };
            PktMonNativeDangerous.Check(PktMonNativeDangerous.CreateStream(handle, ref configuration, out stream));
            PktMonNativeDangerous.Check(PktMonNativeDangerous.Attach(session, stream));
            Volatile.Write(ref active, 1);
            PktMonNativeDangerous.Check(PktMonNativeDangerous.SetActive(session, 1));
            started = true;
        }
        finally
        {
            if (!started)
            {
                Stop();
            }
        }
    }

    /// <summary>ネイティブ通知の寿命内に必要なバイトだけコピーします。</summary>
    private void OnDataDangerous(nint context, nint descriptor)
    {
        try
        {
            PktMonSourceFilter filter = sourceFilter ?? throw new InvalidOperationException("Packet Monitor capture scope is not initialized.");
            (PacketRecord? packet, long missed) = PktMonFrameDangerous.ReadDangerous(descriptor, filter);
            Interlocked.Add(ref dropped, missed);
            if (packet is null)
            {
                Interlocked.Increment(ref suppressed);
            }
            else
            {
                receive?.Invoke(packet);
            }
        }
        catch (Exception exception)when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            // マネージ例外をネイティブコールバックの外へ伝播させません。
            Volatile.Write(ref error, "Packet Monitor: " + exception.Message);
            Interlocked.Increment(ref dropped);
        }
    }

    /// <summary>欠落やストリーム停止の理由を記録します。</summary>
    private void OnEventDangerous(nint context, nint info, int kind)
    {
        if (kind is 1 or 2)
        {
            Volatile.Write(ref active, 0);
        }

        if (kind is 2 or 3 && info != 0)
        {
            int reason = Marshal.ReadInt32(info, 4);
            Volatile.Write(ref error, $"Packet Monitor event {kind}: error {reason}. Packets larger than 9000 bytes cannot be captured.");
        }
    }

    /// <summary>他アプリのセッションには触れず、自身のストリームを終了します。</summary>
    public void Stop()
    {
        Volatile.Write(ref active, 0);
        if (session != 0)
        {
            int result = PktMonNativeDangerous.SetActive(session, 0);
            if (result < 0)
            {
                error = $"Packet Monitor stop: 0x{result:X8}";
            }
        }

        if (stream != 0)
        {
            PktMonNativeDangerous.CloseStream(stream);
            stream = 0;
        }

        if (session != 0)
        {
            PktMonNativeDangerous.CloseSession(session);
            session = 0;
        }

        receive = null;
        sourceFilter = null;
    }

    /// <summary>停止後にハンドルと DLL を解放します。</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Stop();
        PktMonNativeDangerous.Uninitialize(handle);
        handle = 0;
        GC.KeepAlive(dataCallback);
        GC.KeepAlive(eventCallback);
    }
}
