using System.IO.Pipes;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Runtime.Processes.Commands;

namespace ReArtWord.Contents.BackgroundService.Nodes.Scripting;

/// <summary>継続workerのpipeと実プロセスを同じ待受寿命で所有します。</summary>
internal static class CSharpSourceProcess
{
    /// <summary>取消を協調通知し、応答しない子だけを既存のプロセス停止境界へ渡します。</summary>
    /// <returns>完了確認済みの受渡しです。強制停止後は結果不明を通知します。</returns>
    // 抑制理由: 元の取消より、所有する子プロセスの停止不明を優先して報告します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW333", Justification = "強制停止を協調取消の成功として返しません。"),
     System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2219", Justification = "後始末で停止不明が判明した場合は通常取消を上書きして報告します。")]
    internal static async Task RunAsync(ScriptHostCommand host, ScriptWorkspace workspace, CSharpWorkerRequest request, long memory,
        Func<JsonElement, CancellationToken, ValueTask<bool>> receive, CancellationToken cancellationToken = default)
    {
        string name = "contents-script-" + Guid.NewGuid().ToString("N");
        using NamedPipeServerStream pipe = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
        string input = await workspace.WriteAsync(request with { SourcePipe = name }, 16777216, cancellationToken).ConfigureAwait(false);
        using CancellationTokenSource processStop = new();
        Task<string> process = BoundedProcessRunner.RunAsync(host.Executable, [.. host.PrefixArguments, "--script-worker", input],
            new(request.MaximumDiagnosticBytes, true, memory, workspace.DirectoryPath), processStop.Token);
        bool forced = false;
        try
        {
            using CancellationTokenSource admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            admission.CancelAfter(TimeSpan.FromSeconds(15));
            Task connected = pipe.WaitForConnectionAsync(admission.Token);
            try
            {
                if (await Task.WhenAny(connected, process).ConfigureAwait(false) == process && !connected.IsCompleted)
                {
                    await process.ConfigureAwait(false);
                    throw new EndOfStreamException("script.workerDisconnected");
                }
                await connected.ConfigureAwait(false);
            }
            finally
            {
                await admission.CancelAsync().ConfigureAwait(false);
                try
                {
                    await connected.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (admission.IsCancellationRequested)
                {
                }
            }
            bool finished = false;
            while (!finished)
            {
                using JsonDocument frame = await ScriptStreamFrames.ReadAsync(pipe, request.MaximumDiagnosticBytes, cancellationToken).ConfigureAwait(false);
                finished = !await receive(frame.RootElement, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (!process.IsCompleted)
            {
                using CancellationTokenSource grace = new(TimeSpan.FromSeconds(3));
                try
                {
                    if (pipe.IsConnected)
                    {
                        await pipe.WriteAsync(new byte[] { 1 }, grace.Token).ConfigureAwait(false);
                        await pipe.FlushAsync(grace.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception exception) when (exception is OperationCanceledException or IOException)
                {
                    // 最終フレーム直後に子がpipeを閉じても、正常終了を待つ猶予は維持します。
                }
                try
                {
                    await process.WaitAsync(grace.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (grace.IsCancellationRequested)
                {
                    forced = !process.IsCompleted;
                }
                if (forced)
                {
                    await processStop.CancelAsync().ConfigureAwait(false);
                }
            }
            try
            {
                await process.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (processStop.IsCancellationRequested)
            {
                throw new InvalidDataException("script.stopForced");
            }
        }
    }
}
