using System.IO;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.SignalCapture.Capture;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.Protocol;

/// <summary>pipe の切断と要求を監視し、終了時に全キャプチャを回収します。</summary>
internal sealed class PipeHost
{
    private readonly CaptureFactory factory;

    /// <summary>実機またはデモのホストを構成します。</summary>
    internal PipeHost(bool demo, int demoRate = 10) => factory = new CaptureFactory(demo, demoRate);

    /// <summary>入力 EOF と出力切断のどちらでも、ネイティブ資源を先に閉じます。</summary>
    /// <returns>正常終了0、不正フレーム等は1。</returns>
    // 抑制理由: キャンセル不能の標準入力はプロセス終了で回収し、native取得は先にStopして100msの終了猶予を与えます。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2025", Justification = "native取得停止後、標準pipeの保留I/Oは終了猶予またはプロセス終了で回収します。")]
    internal async Task<int> RunAsync(Stream input, Stream output, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource stopped = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var queue = new PacketQueue();
        using var session = new WorkerSession(factory, queue);
        var writer = new PipeOutput(output, queue, session);
        Task writing = writer.RunAsync(stopped.Token);
        Task<int> reading = ReadAsync(input, writer, session, stopped.Token);
        Task statistics = StatisticsAsync(writer, session, stopped.Token);
        Task finished = await Task.WhenAny(reading, writing, statistics).ConfigureAwait(false);
        await stopped.CancelAsync().ConfigureAwait(false);
        session.Stop();
        try
        {
            await finished.ConfigureAwait(false);
            return reading.IsCompletedSuccessfully ? await reading.ConfigureAwait(false)
                                                   : 0;
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or TimeoutException)
        {
            return 0;
        }
        finally
        {
            await FinishTasksAsync(new[] { writing, reading, statistics }, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>制御要求だけを受け付け、入力内容を診断へ転載しません。</summary>
    /// <returns>正常 EOF 0、不正フレーム1。</returns>
    private async Task<int> ReadAsync(Stream input, PipeOutput output, WorkerSession session, CancellationToken cancellationToken = default)
    {
        await output.SendAsync(new PipeEvent("hello", ProtocolVersion: 1), cancellationToken).ConfigureAwait(false);
        while (!cancellationToken.IsCancellationRequested)
        {
            PipeFrame? frame;
            try
            {
                frame = await PipeFraming.ReadAsync(input, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException)
            {
                await output.SendAsync(new PipeEvent("error", Message: "Invalid or incomplete pipe frame."), cancellationToken).ConfigureAwait(false);
                return 1;
            }

            if (frame is null)
            {
                return 0;
            }
            try
            {
                if (frame.Kind != 1)
                {
                    throw new ArgumentException("Control frames must use kind 1.");
                }
                PipeCommand? command = JsonSerializer.Deserialize(frame.Payload, PipeJsonContext.Default.PipeCommand);
                if (command is null)
                {
                    throw new ArgumentException("Invalid control command.");
                }
                PipeEvent response;
                switch (command.Type)
                {
                    case "list":
                        response = factory.Catalog();
                        break;
                    case "start":
                        response = session.Start(command);
                        break;
                    case "stop":
                        response = session.Stop();
                        await output.SendAsync(session.Statistics(), cancellationToken).ConfigureAwait(false);
                        break;
                    case "exclusions":
                        response = session.ReplaceExclusions(command);
                        break;
                    default:
                        throw new ArgumentException("Unknown control command.");
                }

                await output.SendAsync(response, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException)
            {
                await output.SendAsync(new PipeEvent("error", Message: "Invalid capture command or source selection."), cancellationToken).ConfigureAwait(false);
            }
        }

        return 0;
    }

    /// <summary>受信がないときも欠落・抑制とバックエンドの停止を通知します。</summary>
    /// <returns>定期通知処理。</returns>
    private static async Task StatisticsAsync(PipeOutput output, WorkerSession session, CancellationToken cancellationToken = default)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        SourceState[] previous = Array.Empty<SourceState>();
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await output.SendAsync(session.Statistics(), cancellationToken).ConfigureAwait(false);
            PipeEvent state = session.State();
            SourceState[] current = state.Sources ?? Array.Empty<SourceState>();
            if (!previous.SequenceEqual(current))
            {
                previous = current;
                await output.SendAsync(state, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>終了時にキャンセル非対応の標準入出力を待ち続けず、遅延した例外を回収します。</summary>
    /// <returns>完了または短い終了猶予。</returns>
    private static async Task FinishTasksAsync(Task[] tasks, CancellationToken cancellationToken = default)
    {
        try
        {
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or TimeoutException)
        {
        }
    }
}
