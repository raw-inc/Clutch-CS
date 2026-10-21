using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Channels;
using ReArtWord.Contents.BackgroundService.Contracts.Desktop;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop.Tray;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop.Capture;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Desktop;

namespace ReArtWord.Contents.BackgroundService.CoreService.Desktop.Annotations;

/// <summary>閲覧者の指示だけを共有し、実操作APIへは一切渡さない接続単位の表示経路です。</summary>
[SupportedOSPlatform("windows")]
internal sealed class DesktopAnnotations(DesktopMonitor[] monitors, string? group = null)
{
    private readonly Channel<(int X, int Y, int Phase, int Line, int Epoch)> pending = Channel.CreateBounded<(int, int, int, int, int)>(
        new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true });
    private int epoch;
    private bool viewOnly;

    /// <summary>操作可能モードへの復帰時はqueueの古い指示を適用しません。</summary>
    internal void SetViewOnly(bool value)
    {
        Interlocked.Increment(ref epoch);
        Volatile.Write(ref viewOnly, value);
    }

    /// <summary>固定長の表示座標だけを検証します。操作可能な接続は描画送信できません。</summary>
    internal void Apply(ReadOnlySpan<byte> message)
    {
        if (message.Length != DesktopWire.InputSize || BinaryPrimitives.ReadInt32BigEndian(message) != 13)
        {
            throw new InvalidDataException("Invalid viewer annotation.");
        }
        int monitor = BinaryPrimitives.ReadInt32BigEndian(message[4..]), x = BinaryPrimitives.ReadInt32BigEndian(message[8..]),
            y = BinaryPrimitives.ReadInt32BigEndian(message[12..]), phase = BinaryPrimitives.ReadInt32BigEndian(message[16..]), line = BinaryPrimitives.ReadInt32BigEndian(message[20..]);
        if (phase is < 0 or > 2 || line < 0 || (phase == 0 && (monitor != 0 || x != 0 || y != 0 || line != 0)))
        {
            throw new InvalidDataException("Invalid viewer annotation phase.");
        }
        if (phase != 0)
        {
            if (monitor < 0 || monitors.Length <= monitor || x < 0 || y < 0 || monitors[monitor].Width <= x || monitors[monitor].Height <= y)
            {
                throw new InvalidDataException("Viewer annotation is outside the selected monitor.");
            }
            x = checked(x + monitors[monitor].Left); y = checked(y + monitors[monitor].Top);
        }
        if (Volatile.Read(ref viewOnly) || phase == 0)
        {
            pending.Writer.TryWrite((x, y, phase, line, Volatile.Read(ref epoch)));
        }
    }

    /// <summary>native表示とpipe送信を分離し、遅い接続では最新snapshot一つだけ保持します。</summary>
    /// <returns>専用threadと送信の終了確認です。</returns>
    // 抑制理由: finallyで描画threadを回収してからlinked tokenと借用streamを解放します。
    [SuppressMessage("Reliability", "CA2025", Justification = "finallyで専用threadをawaitします。")]
    internal async Task RunAsync(Stream stream, SemaphoreSlim send, CancellationTokenSource lifetime, DesktopFault fault, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Channel<byte[]> frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true });
        Task render = Task.Factory.StartNew(() => Render(frames.Writer, stopping.Token), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            byte[] header = new byte[DesktopWire.HeaderSize];
            await foreach (byte[] payload in frames.Reader.ReadAllAsync(stopping.Token).ConfigureAwait(false))
            {
                DesktopWire.WriteHeader(header, 12, 0, payload.Length, false, 0);
                await send.WaitAsync(stopping.Token).ConfigureAwait(false);
                try
                {
                    await stream.WriteAsync(header, stopping.Token).ConfigureAwait(false);
                    await stream.WriteAsync(payload, stopping.Token).ConfigureAwait(false);
                }
                finally
                {
                    send.Release();
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // 切断に伴う表示停止です。
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            fault.Record("annotations", exception);
            await lifetime.CancelAsync().ConfigureAwait(false);
        }
        finally
        {
            await stopping.CancelAsync().ConfigureAwait(false);
            await render.ConfigureAwait(false);
        }
    }

    /// <summary>一つの専用thread内で、shared memoryとnative windowの寿命を閉じます。</summary>
    private void Render(ChannelWriter<byte[]> frames, CancellationToken cancellationToken)
    {
        List<WindowsAnnotationOverlay> overlays = [];
        try
        {
            using DesktopDpiScope dpi = new();
            using DesktopAnnotationStore store = new(group);
            foreach (DesktopMonitor monitor in monitors)
            {
                overlays.Add(new WindowsAnnotationOverlay());
            }
            long previousRevision = -1, lastSent = 0;
            long paintedRevision = -1;
            bool previousOwner = false;
            bool hadPoints = false;
            int previousEpoch = -1;
            while (!cancellationToken.IsCancellationRequested)
            {
                long now = Environment.TickCount64;
                int currentEpoch = Volatile.Read(ref epoch);
                if (previousEpoch != currentEpoch)
                {
                    store.Publish(0, 0, 0, 0, now);
                    previousEpoch = currentEpoch;
                }
                while (pending.Reader.TryRead(out (int X, int Y, int Phase, int Line, int Epoch) point))
                {
                    if (point.Epoch == currentEpoch && (Volatile.Read(ref viewOnly) || point.Phase == 0))
                    {
                        store.Publish(point.X, point.Y, point.Phase, point.Line, now);
                    }
                }
                DesktopAnnotationSnapshot? snapshot = store.Read(now);
                if (snapshot is not null)
                {
                    if (snapshot.Revision != paintedRevision || snapshot.Owner != previousOwner || snapshot.Points.Length != 0 || hadPoints)
                    {
                        for (int index = 0; index < overlays.Count; index++)
                        {
                            overlays[index].Update(monitors[index], snapshot);
                        }
                        paintedRevision = snapshot.Revision;
                        previousOwner = snapshot.Owner;
                        hadPoints = snapshot.Points.Length != 0;
                    }
                    if (snapshot.Revision != previousRevision || 500 <= now - lastSent)
                    {
                        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(snapshot.Payload(), ContractJson.Options);
                        if (65536 < payload.Length)
                        {
                            throw new InvalidDataException("Viewer annotation snapshot exceeds the wire limit.");
                        }
                        frames.TryWrite(payload);
                        previousRevision = snapshot.Revision; lastSent = now;
                    }
                }
                else
                {
                    paintedRevision = -1;
                    foreach (WindowsAnnotationOverlay overlay in overlays)
                    {
                        overlay.Hide();
                    }
                }
                for (int index = 0; index < 64 && WindowsAnnotationNative.PeekMessageW(out WindowsTrayNative.Message message, IntPtr.Zero, 0, 0, 1); index++)
                {
                    _ = WindowsTrayNative.DispatchMessageW(ref message);
                }
                if (cancellationToken.WaitHandle.WaitOne(40))
                {
                    break;
                }
            }
            frames.TryComplete();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or Win32Exception or UnauthorizedAccessException or ExternalException)
        {
            // 任意表示の初期化失敗は映像・音声・実入力を停止せず、対応UIへ明示します。
            frames.TryWrite(JsonSerializer.SerializeToUtf8Bytes(new { available = false }, ContractJson.Options));
            frames.TryComplete();
        }
        finally
        {
            foreach (WindowsAnnotationOverlay overlay in overlays)
            {
                overlay.Dispose();
            }
        }
    }
}
