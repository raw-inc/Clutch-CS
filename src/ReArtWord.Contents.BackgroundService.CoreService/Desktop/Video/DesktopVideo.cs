using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.Contracts.Desktop;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop.Capture;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop.Input;
using ReArtWord.Contents.BackgroundService.Infrastructure.Desktop;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Desktop;

namespace ReArtWord.Contents.BackgroundService.CoreService.Desktop.Video;

/// <summary>一画面のエンコーダ・受信領域・転送を同じ寿命で所有します。</summary>
internal static class DesktopVideo
{
    /// <summary>実際に一フレームを符号化できた方式だけを選びます。</summary>
    /// <returns>指定コーデックで利用可能なエンコーダです。</returns>
    // 抑制理由: redirect taskはfinallyのStopAsync内で回収してからprocessを解放します。
    [SuppressMessage("Reliability", "CA2025", Justification = "finallyでredirect taskの終了もawaitします。")]
    internal static async Task<DesktopEncoding> SelectEncoderAsync(string executable, DesktopMonitor monitor, EDesktopVideoCodec codec = EDesktopVideoCodec.H264, DesktopVideoSettings? settings = null, CancellationToken cancellationToken = default)
    {
        bool[] captures = [false];
        if (OperatingSystem.IsWindows() && DesktopDuplication.FindOutput(monitor.Name) is not null)
        {
            captures = [true, false];
        }
        IReadOnlyList<string> candidates = codec switch
        {
            EDesktopVideoCodec.H264 => DesktopEncoder.Candidates,
            EDesktopVideoCodec.Vp9 => DesktopEncoder.Vp9Candidates,
            EDesktopVideoCodec.H265 => DesktopEncoder.HevcCandidates,
            _ => throw new ArgumentOutOfRangeException(nameof(codec)),
        };
        foreach (string encoder in candidates)
        {
            if (!DesktopCodecOptions.Supports(encoder, settings))
            {
                continue;
            }
            foreach (bool duplication in captures)
            {
                string? desktop = await WaitForDesktopAsync(cancellationToken).ConfigureAwait(false);
                using Process probe = StartEncoder(DesktopEncoder.StartInfo(executable, monitor, encoder, 0, true, duplication, settings), desktop);
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                Task error = DrainAsync(probe, true, timeout.Token);
                Task output = DrainAsync(probe, false, timeout.Token);
                try
                {
                    await probe.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                    await Task.WhenAll(error, output).ConfigureAwait(false);
                    if (probe.ExitCode == 0)
                    {
                        return new(encoder, duplication, codec);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // 対応名だけが存在するGPUも有限時間で次の候補へ移ります。
                }
                finally
                {
                    await StopAsync(probe, error, output, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        throw new InvalidOperationException("No encoder for the selected codec could capture this interactive desktop.");
    }

    /// <summary>フレームごとのmanaged配列生成を行わず、参照破損後はIDRまで待ちます。</summary>
    /// <returns>切断、encoder終了、取消のいずれかまでの転送です。</returns>
    internal static async Task RunAsync(string executable, DesktopMonitor monitor, DesktopEncoding encoder, Stream stream, SemaphoreSlim send,
        DesktopVideoSettings? settings, CancellationToken sessionCancellationToken, CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? desktop = await WaitForDesktopAsync(cancellationToken).ConfigureAwait(false);
            await RunCaptureAsync(executable, monitor, encoder, stream, send, settings, desktop, sessionCancellationToken, cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>一回のdesktop取得を所有し、入力desktop変更だけを正常な終了として返します。</summary>
    /// <returns>desktop変更による取得終了です。それ以外のencoder終了は例外です。</returns>
    // 抑制理由: 終了監視はfinallyでawaitしてからprocessとlifetimeを解放します。
    [SuppressMessage("Reliability", "CA2025", Justification = "監視taskをfinallyで回収します。"),
     SuppressMessage("SourceQuality", "RAW511", Justification = "条件付きで保持したdesktop監視もfinallyでawaitします。")]
    private static async Task RunCaptureAsync(string executable, DesktopMonitor monitor, DesktopEncoding encoder, Stream stream, SemaphoreSlim send,
        DesktopVideoSettings? settings, string? desktop, CancellationToken sessionCancellationToken, CancellationToken cancellationToken = default)
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        // An access unit can be much larger than its average bitrate, especially
        // at a key frame. Leave room for that burst while the stream is writing.
        socket.ReceiveBufferSize = DesktopWire.MaximumFrameBytes;
        int port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        using Process process = StartEncoder(DesktopEncoder.StartInfo(executable, monitor, encoder.Name, port, duplication: encoder.Duplication, settings: settings), desktop);
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task errors = DrainAsync(process, true, lifetime.Token);
        Task output = DrainAsync(process, false, lifetime.Token);
        Task exited = StopOnExitAsync(process, lifetime, lifetime.Token);
        Task<bool> changed = Task.FromResult(false);
        if (OperatingSystem.IsWindows() && desktop is not null)
        {
            char[] buffer = new char[256];
            changed = WatchDesktopAsync(() => !OperatingSystem.IsWindows() || WindowsInputDesktop.IsCurrent(desktop, buffer), lifetime, lifetime.Token);
        }
        try
        {
            await ForwardAsync(socket, monitor.Id, stream, send, encoder.Codec, sessionCancellationToken, lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // encoderの終了による内部取消を、利用者の切断による取消と区別します。
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            try
            {
                await StopAsync(process, errors, output, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                await Task.WhenAll(exited, changed).ConfigureAwait(false);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (await changed.ConfigureAwait(false) || await ChangedAfterExitAsync(desktop, cancellationToken).ConfigureAwait(false))
        {
            return;
        }
        if (process.ExitCode == 0)
        {
            throw new IOException("Desktop encoder ended before cancellation.");
        }
        throw new IOException("Desktop encoder exited.", process.ExitCode);
    }

    /// <summary>Windowsでは既存権限のまま入力desktopを指定し、他OSでは通常起動します。</summary>
    /// <returns>開始済みencoderです。</returns>
    private static Process StartEncoder(ProcessStartInfo info, string? desktop)
    {
        if (OperatingSystem.IsWindows())
        {
            return DesktopWorkerProcess.StartCapture(info, desktop ?? "default");
        }
        return Process.Start(info) ?? throw new IOException("Desktop encoder did not start.");
    }

    /// <summary>Windowsのnative起動は標準handleを継承しません。</summary>
    /// <returns>所有しているredirect streamだけの回収です。</returns>
    private static Task DrainAsync(Process process, bool error, CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
        {
            return Task.CompletedTask;
        }
        Stream stream = process.StandardOutput.BaseStream;
        if (error)
        {
            stream = process.StandardError.BaseStream;
        }
        return stream.CopyToAsync(Stream.Null, cancellationToken);
    }

    /// <summary>切替中または権限外のdesktopでは取得を待ち、接続と音声を維持します。</summary>
    /// <returns>取得可能なdesktop名です。</returns>
    private static async Task<string?> WaitForDesktopAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        while (!cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? name = WindowsInputDesktop.Name();
            if (name is not null)
            {
                return name;
            }
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        throw new OperationCanceledException(cancellationToken);
    }

    /// <summary>UAC等のdesktop変更で現在の取得だけを中止します。</summary>
    /// <returns>desktop変更を検出したかです。</returns>
    internal static async Task<bool> WatchDesktopAsync(Func<bool> isCurrent, CancellationTokenSource lifetime, CancellationToken cancellationToken = default)
    {
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(100));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!isCurrent())
                {
                    await lifetime.CancelAsync().ConfigureAwait(false);
                    return true;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 取得終了または接続終了で監視も停止します。
        }
        return false;
    }

    /// <summary>DXGIの終了通知がdesktop変更に少し先行する場合だけ確認を待ちます。</summary>
    /// <returns>短い猶予内に入力desktopが変わったかです。</returns>
    private static async Task<bool> ChangedAfterExitAsync(string? desktop, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() || desktop is null)
        {
            return false;
        }
        char[] buffer = new char[256];
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (!WindowsInputDesktop.IsCurrent(desktop, buffer))
            {
                return true;
            }
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>RTP/RTCPの受信順や滞留を接続寿命と分離し、同期済みフレームだけを転送します。</summary>
    /// <returns>受信を中止するまでの有界バッファ処理です。</returns>
    internal static async Task ForwardAsync(Socket socket, int monitorId, Stream stream, SemaphoreSlim send,
        EDesktopVideoCodec codec, CancellationToken sessionCancellationToken, CancellationToken cancellationToken = default)
    {
        using IMemoryOwner<byte> packetOwner = MemoryPool<byte>.Shared.Rent(65536);
        using IMemoryOwner<byte> frameOwner = MemoryPool<byte>.Shared.Rent(DesktopWire.MaximumFrameBytes);
        Memory<byte> packet = packetOwner.Memory[..65536];
        byte[] header = new byte[DesktopWire.HeaderSize];
        byte[] recovery = new byte[DesktopWire.HeaderSize + 8];
        bool published = false;
        bool recoveryPending = false;
        IDesktopRtpAssembler assembler = new H26xRtpAssembler(frameOwner.Memory[..DesktopWire.MaximumFrameBytes], codec == EDesktopVideoCodec.H265);
        if (codec == EDesktopVideoCodec.Vp9)
        {
            assembler = new Vp9RtpAssembler(frameOwner.Memory[..DesktopWire.MaximumFrameBytes]);
        }
        DesktopRtpClock clock = new();
        while (!cancellationToken.IsCancellationRequested)
        {
            int count = await socket.ReceiveAsync(packet, SocketFlags.None, cancellationToken).ConfigureAwait(false);
            if (clock.Observe(packet.Span[..count]))
            {
                continue;
            }
            bool assembled = assembler.Append(packet.Span[..count]);
            if (!assembled && (count < 12 || (packet.Span[1] & 0x80) == 0))
            {
                continue;
            }
            long unix = (DateTimeOffset.UtcNow.Ticks - DateTimeOffset.UnixEpoch.Ticks) / 10;
            long monotonic = (long)(Stopwatch.GetTimestamp() * (1_000_000d / Stopwatch.Frequency));
            long? captured = null;
            if (assembled)
            {
                captured = clock.MapFrame(packet.Span[..count], unix, monotonic, assembler.KeyFrame);
            }
            if (captured is null)
            {
                // A completed but unusable access unit must not look like an
                // idle desktop forever. The browser gives a natural key a grace
                // period, then reselects video without resetting audio or input.
                if (published && !recoveryPending && 12 <= count && (packet.Span[1] & 0x80) != 0)
                {
                    DesktopWire.WriteHeader(recovery.AsSpan(0, DesktopWire.HeaderSize), 9, monitorId, 8, true, monotonic);
                    await send.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        await stream.WriteAsync(recovery, sessionCancellationToken).ConfigureAwait(false);
                        recoveryPending = true;
                    }
                    finally
                    {
                        send.Release();
                    }
                }
                continue;
            }
            DesktopWire.WriteHeader(header, 2, monitorId, assembler.Frame.Length, assembler.KeyFrame, captured.Value);
            await send.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // 設定切替では一枚を途中で切らず、接続自体の終了時だけ書込みを中止します。
                await stream.WriteAsync(header, sessionCancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(assembler.Frame, sessionCancellationToken).ConfigureAwait(false);
                published = true;
                recoveryPending = false;
            }
            finally
            {
                send.Release();
            }
        }
    }

    /// <summary>プロセスが終了したら無期限のUDP待受も中止します。</summary>
    /// <returns>監視終了です。</returns>
    private static async Task StopOnExitAsync(Process process, CancellationTokenSource lifetime, CancellationToken cancellationToken = default)
    {
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await lifetime.CancelAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 接続側が停止した場合も監視タスクを回収します。
        }
    }

    /// <summary>外部プロセスとredirectされた両パイプを回収します。</summary>
    /// <returns>OSによるプロセス終了の確認です。</returns>
    private static async Task StopAsync(Process process, Task error, Task output, CancellationToken cancellationToken = default)
    {
        if (!process.HasExited)
        {
            process.Kill(true);
        }
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.WhenAll(error, output).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 有限probeとセッション終了の取消を回収します。
        }
    }
}
