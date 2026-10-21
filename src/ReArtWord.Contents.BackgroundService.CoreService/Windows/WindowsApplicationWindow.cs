using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Runtime.Versioning;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop;

namespace ReArtWord.Contents.BackgroundService.CoreService.Windows;

/// <summary>Session 0からも、指定した一つのアプリケーションへ通常のウィンドウ終了を要求します。</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsApplicationWindow
{
    /// <summary>Core自身の固定補助モードだけを相手にし、PIDと起動時刻を照合して終了を要求します。</summary>
    /// <returns>WM_CLOSE要求を送信できたかです。</returns>
    internal static async Task<bool> CloseAsync(ProcessIdentity identity, int session, CancellationToken cancellationToken = default)
    {
        string name = "contents-application-close-" + Guid.NewGuid().ToString("N");
        using NamedPipeServerStream pipe = DesktopWorkerProcess.CreatePipe(name);
        using Process worker = DesktopWorkerProcess.StartApplicationClose(name, session);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            DesktopWorkerProcess.Verify(pipe, worker);
            byte[] request = new byte[12];
            BinaryPrimitives.WriteInt32LittleEndian(request, identity.ProcessId);
            BinaryPrimitives.WriteInt64LittleEndian(request.AsSpan(4), identity.StartedAt.UtcTicks);
            await pipe.WriteAsync(request, timeout.Token).ConfigureAwait(false);
            byte[] response = new byte[1];
            await pipe.ReadExactlyAsync(response, timeout.Token).ConfigureAwait(false);
            return response[0] == 1;
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
        finally
        {
            if (!worker.HasExited)
            {
                worker.Kill(true);
            }
            using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(5));
            await worker.WaitForExitAsync(cleanup.Token).ConfigureAwait(false);
        }
    }

    /// <summary>引数に操作対象を含めず、親が確認したpipeから一件だけ読みます。</summary>
    /// <returns>対象確認と応答送信の完了です。</returns>
    // 抑制理由: OS起動時刻のBCL値を直ちにUTC tickへ変換し、PID再利用を防ぎます。
    [SuppressMessage("SourceQuality", "RAW171", Justification = "OS起動時刻を照合するため、BCLのStartTimeをUTCへ正規化します。")]
    internal static async Task RunAsync(string name, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using NamedPipeClientStream pipe = new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        byte[] request = new byte[12];
        await pipe.ReadExactlyAsync(request, timeout.Token).ConfigureAwait(false);
        bool sent = false;
        try
        {
            using Process process = Process.GetProcessById(BinaryPrimitives.ReadInt32LittleEndian(request));
            using Process current = Process.GetCurrentProcess();
            if (process.SessionId == current.SessionId && new DateTimeOffset(process.StartTime.ToUniversalTime()).UtcTicks == BinaryPrimitives.ReadInt64LittleEndian(request.AsSpan(4)))
            {
                sent = process.CloseMainWindow();
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 不明な実体を終了させず、呼出元が選んだ停止方針へ戻します。
        }
        byte[] response = [0];
        if (sent)
        {
            response[0] = 1;
        }
        await pipe.WriteAsync(response, timeout.Token).ConfigureAwait(false);
    }
}
