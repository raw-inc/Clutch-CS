using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using ReArtWord.Contents.BackgroundService.SignalCapture.Capture;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.Protocol;

/// <summary>制御と原本を一つの書き込み処理に集約し、フレームの交錯を防ぎます。</summary>
internal sealed class PipeOutput
{
    private readonly Stream output;
    private readonly PacketQueue queue;
    private readonly WorkerSession session;
    private readonly Channel<ControlEnvelope> controls = Channel.CreateBounded<ControlEnvelope>(16);
    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(1);
    private readonly TimeSpan writeTimeout;

    /// <summary>出力の待機上限を設定します。</summary>
    internal PipeOutput(Stream output, PacketQueue queue, WorkerSession session, TimeSpan? writeTimeout = null)
    {
        this.output = output;
        this.queue = queue;
        this.session = session;
        this.writeTimeout = writeTimeout ?? TimeSpan.FromSeconds(5);
        queue.Available = Signal;
    }

    /// <summary>制御フレームを書き終えるまで待ちます。</summary>
    /// <returns>出力完了。</returns>
    internal async Task SendAsync(PipeEvent message, CancellationToken cancellationToken = default)
    {
        var envelope = new ControlEnvelope(message, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        await controls.Writer.WriteAsync(envelope, cancellationToken).ConfigureAwait(false);
        Signal();
        await envelope.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>キャプチャコールバックからは待機せず通知だけを投入します。</summary>
    private void Signal() => wake.Writer.TryWrite(true);

    /// <summary>制御を優先しつつ原本を順番に出力します。</summary>
    /// <returns>切断・キャンセルまでの出力処理。</returns>
    internal async Task RunAsync(CancellationToken cancellationToken = default)
    {
        ControlEnvelope? current = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (controls.Reader.TryRead(out current))
                {
                    // 待機中に stop/start が進んでも、古い稼働状態や前世代の統計を後から出しません。
                    PipeEvent message = current.Message.Type switch
                    {
                        "state" => session.State(),
                        "stats" => session.Statistics(),
                        _ => current.Message
                    };
                    byte[] json = JsonSerializer.SerializeToUtf8Bytes(message, PipeJsonContext.Default.PipeEvent);
                    await WriteAsync(PipeFraming.Control(json), cancellationToken).ConfigureAwait(false);
                    current.Completion.TrySetResult();
                    current = null;
                    continue;
                }

                QueuedPacket? packet = queue.Read();
                if (packet is not null)
                {
                    byte[]? bytes = session.Encode(packet);
                    if (bytes is not null)
                    {
                        await WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    }
                    continue;
                }

                await wake.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            current?.Completion.TrySetCanceled(cancellationToken);
            while (controls.Reader.TryRead(out ControlEnvelope? pending))
            {
                pending.Completion.TrySetCanceled(cancellationToken);
            }
        }
    }

    /// <summary>OS の標準出力がキャンセルを扱わない場合でも、資源終了を無期限に待たせません。</summary>
    /// <returns>一フレームの書き込み。</returns>
    private async Task WriteAsync(byte[] bytes, CancellationToken cancellationToken = default)
    {
        await output.WriteAsync(bytes, cancellationToken).AsTask().WaitAsync(writeTimeout, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).WaitAsync(writeTimeout, cancellationToken).ConfigureAwait(false);
    }
}
