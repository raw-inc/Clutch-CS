using System.ComponentModel;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Desktop;

namespace ReArtWord.Contents.BackgroundService.CoreService.Desktop.Clipboard;

/// <summary>接続ごとの購読と更新番号を所有し、文字列を履歴へ残さず反映確認を返します。</summary>
internal sealed class DesktopClipboard(Func<uint> sequence, Func<string?> read, Func<string, bool> write)
{
    private readonly Lock gate = new();
    private bool enabled;
    private bool viewOnly;
    private uint observed;
    private int generation;

    /// <summary>有効化時の既存ホスト内容を送り返さず、以後の変更だけを観測します。</summary>
    internal void Configure(bool value)
    {
        using (gate.EnterScope())
        {
            generation++;
            enabled = value && !viewOnly;
            if (enabled)
            {
                observed = sequence();
            }
        }
    }

    /// <summary>閲覧専用への変更は待機中の書換えと購読も無効にします。</summary>
    internal void SetViewOnly(bool value)
    {
        using (gate.EnterScope())
        {
            viewOnly = value;
            if (value)
            {
                enabled = false;
                generation++;
            }
        }
    }

    /// <summary>更新番号が変化した場合だけ本文を取得し、遅い接続へ無制限に蓄積しません。</summary>
    /// <returns>購読の監視終了です。</returns>
    internal async Task RunAsync(Stream stream, SemaphoreSlim send, CancellationTokenSource lifetime, DesktopFault fault, CancellationToken cancellationToken = default)
    {
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(100));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                (byte[]? frame, int epoch) = ReadChange();
                if (frame is not null)
                {
                    await SendAsync(frame, epoch, stream, send, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            fault.Record("clipboard", exception);
            await lifetime.CancelAsync().ConfigureAwait(false);
        }
    }

    /// <summary>切替後の古い要求を破棄し、OSが使用中で未変更だった場合だけ短く再試行します。</summary>
    /// <returns>反映確認または入力本文を含まない失敗通知です。</returns>
    internal async Task WriteAsync(int requestId, ReadOnlyMemory<byte> bytes, Stream stream, SemaphoreSlim send, CancellationToken cancellationToken = default)
    {
        string text = DesktopClipboardWire.ReadText(bytes.Span);
        int epoch;
        using (gate.EnterScope())
        {
            epoch = generation;
        }
        byte[]? response = null;
        for (int attempt = 0; attempt < 4 && response is null; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (gate.EnterScope())
            {
                if (!enabled || viewOnly || epoch != generation)
                {
                    response = DesktopClipboardWire.Frame(2, requestId, 0, "Clipboard.Disabled");
                }
                else
                {
                    try
                    {
                        if (write(text))
                        {
                            observed = sequence();
                            response = DesktopClipboardWire.Frame(1, requestId, observed);
                        }
                    }
                    catch (Win32Exception)
                    {
                        response = DesktopClipboardWire.Frame(2, requestId, 0, "Clipboard.WriteFailed");
                    }
                }
            }
            if (response is null && attempt < 3)
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }
        response ??= DesktopClipboardWire.Frame(2, requestId, 0, "Clipboard.Busy");
        await SendAsync(response, epoch, stream, send, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>未購読時はOSへ触れず、本文取得中の別更新は次の観測へ回します。</summary>
    /// <returns>一件の変更フレームと、その購読世代です。</returns>
    private (byte[]? Frame, int Epoch) ReadChange()
    {
        int epoch;
        uint previous;
        using (gate.EnterScope())
        {
            if (!enabled)
            {
                return (null, generation);
            }
            epoch = generation;
            previous = observed;
        }
        uint current = sequence();
        if (current == previous)
        {
            return (null, epoch);
        }
        byte[]? frame = null;
        try
        {
            // 遅延レンダリングを行う他アプリの応答待ちで、購読停止や入力側の状態変更を止めません。
            string? text = read();
            if (text is not null)
            {
                frame = DesktopClipboardWire.Frame(0, 0, current, text);
            }
        }
        catch (Win32Exception)
        {
            // 他アプリが使用中の読み取りだけを次の更新番号確認で再試行します。
            return (null, epoch);
        }
        catch (Exception exception) when (exception is InvalidDataException or System.Text.EncoderFallbackException)
        {
            frame = DesktopClipboardWire.Frame(2, 0, current, "Clipboard.InvalidOrLargeText");
        }
        using (gate.EnterScope())
        {
            if (!enabled || epoch != generation || sequence() != current)
            {
                return (null, epoch);
            }
            observed = current;
            return (frame, epoch);
        }
    }

    /// <summary>入力列の反映確認を映像と直列化し、無効化後の古い通知は破棄します。</summary>
    /// <returns>現在接続への送信です。</returns>
    private async Task SendAsync(byte[] frame, int epoch, Stream stream, SemaphoreSlim send, CancellationToken cancellationToken = default)
    {
        await send.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using (gate.EnterScope())
            {
                if (epoch != generation)
                {
                    return;
                }
            }
            await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            send.Release();
        }
    }
}
