using System.Text.Json;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Desktop;

namespace ReArtWord.Contents.BackgroundService.CoreService.Desktop;

/// <summary>最初の切断原因だけを保持し、秘密やパスを含む例外本文を送信しません。</summary>
internal sealed class DesktopFault
{
    private Failure? failure;

    /// <summary>二次的な取消で元の映像・音声障害を上書きしません。</summary>
    internal void Record(string stage, Exception exception)
    {
        if (exception is not OperationCanceledException)
        {
            Exception cause = exception.GetBaseException();
            int code = cause.HResult;
            if (cause is System.ComponentModel.Win32Exception native)
            {
                code = native.NativeErrorCode;
            }
            Interlocked.CompareExchange(ref failure, new(stage, cause.GetType().Name, code.ToString("X8", System.Globalization.CultureInfo.InvariantCulture)), null);
        }
    }

    /// <summary>資源回収後、接続がまだ使える場合だけ有限時間で診断を通知します。</summary>
    /// <returns>一度の通知試行です。</returns>
    internal async Task SendAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        if (failure is null)
        {
            return;
        }
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(failure, Contracts.Serialization.ContractJson.Options);
        byte[] header = new byte[DesktopWire.HeaderSize];
        DesktopWire.WriteHeader(header, 8, 0, payload.Length, false, 0);
        try
        {
            await stream.WriteAsync(header, timeout.Token).ConfigureAwait(false);
            await stream.WriteAsync(payload, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            // 切断済みの相手へ診断を再送しません。
        }
    }

    /// <summary>画面へ渡す有限の診断です。</summary>
    private sealed record Failure(string Stage, string Type, string Code);
}
