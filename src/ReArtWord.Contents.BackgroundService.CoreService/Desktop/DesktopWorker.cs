using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Desktop;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop.Audio;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop.Annotations;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop.Capture;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop.Clipboard;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop.Input;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop.Video;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Desktop;

namespace ReArtWord.Contents.BackgroundService.CoreService.Desktop;

/// <summary>Coreが起動した対話ユーザー内だけで画面と入力を処理します。</summary>
[SupportedOSPlatform("windows")]
internal static class DesktopWorker
{
    /// <summary>親が所有するローカルpipeへ接続し、切断ですべて停止します。</summary>
    /// <remarks>注釈の共有名は内部呼出しでのみ指定し、通常起動では既定のセッション共有を使います。</remarks>
    /// <returns>接続の終了です。</returns>
    // 抑制理由: 映像・音声・生存・カーソル通知をfinallyで回収してからpipe、入力、sendを解放します。
    [SuppressMessage("SourceQuality", "RAW511", Justification = "保持した映像を切替時、接続寿命の全taskをfinallyでawaitします。"),
     SuppressMessage("Reliability", "CA2025", Justification = "finallyで全taskをawaitしてから借用資源を解放します。") ]
    internal static async Task RunAsync(string pipeName, string executable, string? annotationGroup = null, CancellationToken cancellationToken = default)
    {
        using NamedPipeClientStream pipe = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(10000, cancellationToken).ConfigureAwait(false);
        DesktopMonitor[] monitors = WindowsDesktop.Monitors();
        bool? mousePresent = WindowsPointerDevices.Read();
        for (int index = 0; index < monitors.Length; index++)
        {
            monitors[index] = monitors[index] with { MousePresent = mousePresent };
        }
        using DesktopInput input = new(monitors);
        using SemaphoreSlim send = new(1, 1);
        byte[] metadata = JsonSerializer.SerializeToUtf8Bytes(monitors, ContractJson.Options);
        byte[] header = new byte[DesktopWire.HeaderSize];
        DesktopWire.WriteHeader(header, 1, 0, metadata.Length, false, 0);
        await pipe.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await pipe.WriteAsync(metadata, cancellationToken).ConfigureAwait(false);
        Tray.DesktopTrayPresence tray = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable trayLifetime = tray.ConfigureAwait(false);
        byte[] message = new byte[DesktopWire.InputSize];
        DesktopEncoding?[][] modes = [new DesktopEncoding?[monitors.Length], new DesktopEncoding?[monitors.Length], new DesktopEncoding?[monitors.Length]];
        DesktopVideoSettings? previousSettings = null;
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationTokenSource? capture = null;
        Task video = Task.CompletedTask;
        DesktopFault fault = new();
        DesktopAudio audioCapture = new();
        Task audio = audioCapture.RunAsync(pipe, send, lifetime, fault, lifetime.Token);
        Task heartbeat = Task.CompletedTask;
        Task cursor = Task.CompletedTask;
        Task annotationTask = Task.CompletedTask;
        Task clipboardTask = Task.CompletedTask;
        DesktopClipboard clipboard = new(WindowsDesktopClipboard.Sequence, WindowsDesktopClipboard.ReadText, WindowsDesktopClipboard.TryWriteText);
        DesktopAnnotations? annotations = null;
        bool cursorRequested = false;
        DesktopCodecSettings? codecDetails = null;
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await pipe.ReadExactlyAsync(message, lifetime.Token).ConfigureAwait(false);
                int kind = BinaryPrimitives.ReadInt32BigEndian(message);
                if (kind == DesktopClipboardWire.SubscriptionKind)
                {
                    _ = DesktopClipboardWire.InputLength(message);
                    bool enabled = BinaryPrimitives.ReadInt32BigEndian(message.AsSpan(4)) == 1;
                    clipboard.Configure(enabled);
                    if (enabled && clipboardTask.IsCompleted)
                    {
                        clipboardTask = clipboard.RunAsync(pipe, send, lifetime, fault, lifetime.Token);
                    }
                    continue;
                }
                if (kind == DesktopClipboardWire.WriteKind)
                {
                    byte[] text = new byte[DesktopClipboardWire.InputLength(message) - DesktopWire.InputSize];
                    await pipe.ReadExactlyAsync(text, lifetime.Token).ConfigureAwait(false);
                    await clipboard.WriteAsync(BinaryPrimitives.ReadInt32BigEndian(message.AsSpan(4)), text, pipe, send, lifetime.Token).ConfigureAwait(false);
                    continue;
                }
                if (BinaryPrimitives.ReadInt32BigEndian(message) == 12)
                {
                    if (BinaryPrimitives.ReadInt32BigEndian(message.AsSpan(4)) != 1 || message.AsSpan(8).ContainsAnyExcept((byte)0))
                    {
                        throw new InvalidDataException("Invalid annotation subscription.");
                    }
                    if (annotations is null)
                    {
                        annotations = new(monitors, annotationGroup);
                        annotations.SetViewOnly(input.ViewOnly);
                        annotationTask = annotations.RunAsync(pipe, send, lifetime, fault, lifetime.Token);
                    }
                    continue;
                }
                if (BinaryPrimitives.ReadInt32BigEndian(message) == 13)
                {
                    if (annotations is null)
                    {
                        throw new InvalidDataException("Viewer annotations were not negotiated.");
                    }
                    annotations.Apply(message);
                    continue;
                }
                if (BinaryPrimitives.ReadInt32BigEndian(message) == 9)
                {
                    codecDetails = DesktopCodecOptions.Read(message);
                    continue;
                }
                if (BinaryPrimitives.ReadInt32BigEndian(message) == 8)
                {
                    if (BinaryPrimitives.ReadInt32BigEndian(message.AsSpan(4)) != 1 || message.AsSpan(8).ContainsAnyExcept((byte)0))
                    {
                        throw new InvalidDataException("Invalid cursor subscription.");
                    }
                    if (!cursorRequested)
                    {
                        cursorRequested = true;
                        WindowsDesktopCursor source = new(monitors);
                        cursor = DesktopCursor.RunAsync(source.Read, pipe, send, lifetime, fault, lifetime.Token);
                    }
                    continue;
                }
                if (BinaryPrimitives.ReadInt32BigEndian(message) == 7)
                {
                    audioCapture.Configure(new DesktopAudioSettings(BinaryPrimitives.ReadInt32BigEndian(message.AsSpan(4)), BinaryPrimitives.ReadInt32BigEndian(message.AsSpan(8))));
                    continue;
                }
                if (BinaryPrimitives.ReadInt32BigEndian(message) != 6)
                {
                    input.Apply(message);
                    if (BinaryPrimitives.ReadInt32BigEndian(message) == 11)
                    {
                        tray.SetViewOnly(input.ViewOnly);
                        annotations?.SetViewOnly(input.ViewOnly);
                        clipboard.SetViewOnly(input.ViewOnly);
                    }
                    continue;
                }
                int selected = BinaryPrimitives.ReadInt32BigEndian(message.AsSpan(4));
                int codec = BinaryPrimitives.ReadInt32BigEndian(message.AsSpan(8));
                int selection = BinaryPrimitives.ReadInt32BigEndian(message.AsSpan(12));
                bool heartbeatRequested = BinaryPrimitives.ReadInt32BigEndian(message.AsSpan(16)) == 1;
                DesktopVideoSettings settings = DesktopVideoSettings.FromWire(BinaryPrimitives.ReadInt32BigEndian(message.AsSpan(20)), codec == 1) with { CodecDetails = codecDetails };
                codecDetails?.Validate(codec == 1);
                settings.Validate();
                if (selected < -1 || monitors.Length <= selected || codec is < 0 or > 2)
                {
                    throw new InvalidDataException("Unknown desktop monitor.");
                }
                if (heartbeatRequested && heartbeat.IsCompleted)
                {
                    heartbeat = DesktopHeartbeat.RunAsync(pipe, send, lifetime, fault, lifetime.Token);
                }
                DesktopEncoding?[] encoders = modes[codec];
                if (previousSettings != settings)
                {
                    foreach (DesktopEncoding?[] mode in modes)
                    {
                        Array.Clear(mode);
                    }
                    previousSettings = settings;
                }
                input.Release();
                await StopCaptureAsync(capture, video, CancellationToken.None).ConfigureAwait(false);
                capture?.Dispose();
                capture = null;
                video = Task.CompletedTask;
                List<DesktopMonitor> active = monitors.Where(monitor => selected == -1 || monitor.Id == selected).ToList();
                foreach (DesktopMonitor monitor in active)
                {
                    encoders[monitor.Id] ??= await DesktopVideo.SelectEncoderAsync(executable, monitor, (EDesktopVideoCodec)codec, settings, lifetime.Token).ConfigureAwait(false);
                }
                byte[] status = JsonSerializer.SerializeToUtf8Bytes(active.Select(monitor => new { monitor.Id, Encoder = encoders[monitor.Id]!.Name, DesktopDuplication = encoders[monitor.Id]!.Duplication, Codec = codec, Selection = selection, Heartbeat = heartbeatRequested,
                    settings.FramesPerSecond, MaximumBitrateKbps = settings.CodecDetails?.Quality is null ? settings.MaximumBitrateKbps
                                                                                                          : (int?)null,
                    settings.HideCursor, settings.CodecDetails, settings.VariableBitrate, EffectiveKeyFrameInterval = DesktopCodecOptions.KeyFrameLimit(settings), ScanCodeInput = true, UnicodeInput = true, CursorVisibility = true, CursorShapes = true, AdministratorInput = Windows.WindowsInteractiveIdentity.IsAdministrator() }), ContractJson.Options);
                DesktopWire.WriteHeader(header, 3, 0, status.Length, false, 0);
                await send.WaitAsync(lifetime.Token).ConfigureAwait(false);
                try
                {
                    await pipe.WriteAsync(header, lifetime.Token).ConfigureAwait(false);
                    await pipe.WriteAsync(status, lifetime.Token).ConfigureAwait(false);
                }
                finally
                {
                    send.Release();
                }
                capture = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                video = CaptureAsync(active, encoders, executable, pipe, send, capture, lifetime, fault, settings, capture.Token);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or ArgumentException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            fault.Record("session", exception);
        }
        finally
        {
            await tray.DisposeAsync().ConfigureAwait(false);
            await lifetime.CancelAsync().ConfigureAwait(false);
            try
            {
                try
                {
                    await StopCaptureAsync(capture, video, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    fault.Record("video", exception);
                }
            }
            finally
            {
                capture?.Dispose();
                try
                {
                    await Task.WhenAll(audio, heartbeat, cursor, annotationTask, clipboardTask).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                    // 専用threadの開始前に取消された場合も、先に起きた映像障害を通知します。
                }
                await fault.SendAsync(pipe, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>一画面の失敗で接続を閉じ、残りのencoderも停止します。</summary>
    /// <returns>全画面タスクの回収です。</returns>
    private static async Task CaptureAsync(IReadOnlyList<DesktopMonitor> monitors, DesktopEncoding?[] encoders, string executable, Stream pipe,
        SemaphoreSlim send, CancellationTokenSource capture, CancellationTokenSource lifetime, DesktopFault fault, DesktopVideoSettings settings, CancellationToken cancellationToken = default)
    {
        Task[] tasks = monitors.Select(monitor => DesktopVideo.RunAsync(executable, monitor, encoders[monitor.Id]!, pipe, send, settings, lifetime.Token, cancellationToken)).ToArray();
        try
        {
            Task ended = await Task.WhenAny(tasks).ConfigureAwait(false);
            if (ended.Exception is { } exception)
            {
                fault.Record("video", exception);
            }
            if (!cancellationToken.IsCancellationRequested)
            {
                await lifetime.CancelAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            await capture.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }

    /// <summary>画面切替後へ旧フレームが漏れないよう完了を待ちます。</summary>
    /// <returns>切替前のencoderの回収です。</returns>
    private static async Task StopCaptureAsync(CancellationTokenSource? capture, Task video, CancellationToken cancellationToken = default)
    {
        if (capture is null)
        {
            return;
        }
        try
        {
            await capture.CancelAsync().ConfigureAwait(false);
            await video.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (capture.IsCancellationRequested)
        {
            // 選択変更または接続終了による既定の停止です。
        }

    }
}
