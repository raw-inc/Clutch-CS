using System.IO.Pipes;
using System.Reflection;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;

namespace ReArtWord.Contents.BackgroundService.Nodes.Scripting;

/// <summary>子プロセスで列挙を進め、親の取消・切断時には列挙子を解放します。</summary>
internal static class CSharpSourceWorker
{
    /// <summary>出力方向と取消方向を分け、親が受信できる速度だけ列挙します。</summary>
    /// <returns>列挙の終了、例外診断、または取消による解放です。</returns>
    // 抑制理由: 自由記述コードの障害境界であり、例外を有限の診断へ変換します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "子プロセス内の利用者コードの障害境界です。"),
     System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2025", Justification = "finallyで制御監視を取り消し、await完了後にpipeとCTSを解放します。")]
    internal static async Task RunAsync(CSharpWorkerRequest request, CancellationToken cancellationToken = default)
    {
        using NamedPipeClientStream pipe = new(".", request.SourcePipe!, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(10000, cancellationToken).ConfigureAwait(false);
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using CancellationTokenSource control = new();
        Task stopping = WatchStopAsync(pipe, lifetime, control.Token);
        int maximum = Math.Clamp(request.MaximumDiagnosticBytes, 1024, 16777216);
        try
        {
            object source = CSharpScriptInvocation.Invoke(request, lifetime.Token) ?? throw new InvalidDataException("script.sourceMissing");
            Type sequence = source.GetType().GetInterfaces().Single(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>));
            MethodInfo stream = typeof(CSharpSourceWorker).GetMethod(nameof(StreamAsync), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(sequence.GetGenericArguments());
            await ((Task)stream.Invoke(null, [source, pipe, maximum, lifetime.Token])!).ConfigureAwait(false);
            await ScriptStreamFrames.WriteAsync(pipe, new { kind = "completed" }, maximum, lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            // 親が取消結果を保持します。ここでは列挙子のfinallyを完了させます。
        }
        catch (Exception exception)
        {
            if (!lifetime.IsCancellationRequested)
            {
                try
                {
                    await ScriptStreamFrames.WriteAsync(pipe, new { kind = "failed", exception = ExceptionDiagnostic.Capture(exception) }, maximum, lifetime.Token).ConfigureAwait(false);
                }
                catch (InvalidDataException)
                {
                    await ScriptStreamFrames.WriteAsync(pipe, new { kind = "failed", exception = new ExceptionDiagnostic("ScriptError", "[diagnostic omitted: output limit]", null, 0, [], true) }, maximum, lifetime.Token).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await control.CancelAsync().ConfigureAwait(false);
            await stopping.ConfigureAwait(false);
        }
    }

    /// <summary>返却型を保持したまま列挙し、取消時にもDisposeAsyncを実行します。</summary>
    /// <returns>全要素の受渡しです。</returns>
    private static async Task StreamAsync<T>(IAsyncEnumerable<T> source, Stream pipe, int maximum, CancellationToken cancellationToken = default)
    {
        await foreach (T value in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            string valueKind = value is byte[] ? "binary"
                                               : "json";
            await ScriptStreamFrames.WriteAsync(pipe, new { kind = "item", value, valueKind }, maximum, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>取消通知または親の切断を、利用者のCancellationTokenへ反映します。</summary>
    /// <returns>制御方向の監視終了です。</returns>
    private static async Task WatchStopAsync(Stream pipe, CancellationTokenSource lifetime, CancellationToken cancellationToken = default)
    {
        try
        {
            byte[] signal = new byte[1];
            _ = await pipe.ReadAsync(signal, cancellationToken).ConfigureAwait(false);
            await lifetime.CancelAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
