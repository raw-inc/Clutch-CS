using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Desktop;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Desktop;

namespace ReArtWord.Contents.BackgroundService.CoreService.Desktop.Audio;

/// <summary>WASAPI取得、固定PCMバッファ、選択したOpus encoderを対話workerの専用スレッドで所有します。</summary>
[SupportedOSPlatform("windows")]
internal sealed class DesktopAudio
{
    private static readonly string[] codecs = ["f32le", "opus"];
    private DesktopAudioSettings selected = new();

    /// <summary>取得スレッドの次の境界で設定を切り替えます。</summary>
    internal void Configure(DesktopAudioSettings settings)
    {
        settings.Validate();
        Volatile.Write(ref selected, settings);
    }

    /// <summary>画面切替と独立した一つの音声時計を接続全体で維持します。</summary>
    /// <returns>workerが終了時にawaitする専用スレッドのtaskです。</returns>
    internal Task RunAsync(Stream stream, SemaphoreSlim send, CancellationTokenSource lifetime, DesktopFault fault, CancellationToken cancellationToken = default)
        => Task.Factory.StartNew(() => Capture(stream, send, lifetime, fault, cancellationToken), cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary>COM apartment、event、capture packetを同じスレッドで解放します。</summary>
    private void Capture(Stream stream, SemaphoreSlim send, CancellationTokenSource lifetime, DesktopFault fault, CancellationToken cancellationToken)
    {
        DesktopOpusEncoder? opus = null;
        try
        {
            using WindowsLoopbackCapture capture = new();
            float[] samples = new float[16384];
            byte[] header = new byte[DesktopWire.HeaderSize];
            byte[] silence = new byte[8];
            DesktopAudioSettings? active = null;
            WaitHandle[] signals = [capture.Available, cancellationToken.WaitHandle];
            while (!cancellationToken.IsCancellationRequested)
            {
                DesktopAudioSettings settings = Volatile.Read(ref selected);
                if (settings != active)
                {
                    opus?.Dispose();
                    opus = null;
                    if (settings.Codec == 1)
                    {
                        opus = new(settings.BitrateKbps, packet => Write(stream, send, packet.Span, ReadOnlySpan<byte>.Empty, cancellationToken));
                    }
                    string formatName = "f32le";
                    if (opus is not null)
                    {
                        formatName = "opus";
                    }
                    byte[] format = JsonSerializer.SerializeToUtf8Bytes(new { sampleRate = 48000, channels = 2, format = formatName,
                        bitrateKbps = settings.BitrateKbps, codecs, opusBitrates = DesktopAudioSettings.OpusBitrates });
                    DesktopWire.WriteHeader(header, 4, 0, format.Length, false, 0);
                    Write(stream, send, header, format, cancellationToken);
                    active = settings;
                }
                int signal = WaitHandle.WaitAny(signals, 50);
                if (signal == WaitHandle.WaitTimeout)
                {
                    opus?.FlushTail();
                    continue;
                }
                if (signal != 0)
                {
                    break;
                }
                while (!cancellationToken.IsCancellationRequested && capture.Read(samples, out int count, out long timestamp, out bool reset))
                {
                    if (count == 0)
                    {
                        continue;
                    }
                    if (opus is not null)
                    {
                        opus.Append(samples.AsSpan(0, count), timestamp, reset);
                        continue;
                    }
                    if (samples.AsSpan(0, count).ContainsAnyExcept(0f))
                    {
                        ReadOnlySpan<byte> payload = MemoryMarshal.AsBytes(samples.AsSpan(0, count));
                        DesktopWire.WriteHeader(header, 5, 0, payload.Length, reset, timestamp);
                        Write(stream, send, header, payload, cancellationToken);
                    }
                    else
                    {
                        BinaryPrimitives.WriteInt32BigEndian(silence, count / 2);
                        DesktopWire.WriteHeader(header, 6, 0, silence.Length, reset, timestamp);
                        Write(stream, send, header, silence, cancellationToken);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or COMException or InvalidOperationException or OperationCanceledException or Concentus.OpusException)
        {
            fault.Record("audio", exception);
        }
        finally
        {
            opus?.Dispose();
            lifetime.Cancel();
        }
    }

    /// <summary>音声と映像のheader/payloadを途中で混在させません。</summary>
    private static void Write(Stream stream, SemaphoreSlim send, ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload, CancellationToken cancellationToken)
    {
        send.Wait(cancellationToken);
        try
        {
            stream.Write(header);
            stream.Write(payload);
        }
        finally
        {
            send.Release();
        }
    }
}
