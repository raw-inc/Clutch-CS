using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop;
using ReArtWord.Contents.BackgroundService.Runtime.Processes;

namespace ReArtWord.Contents.BackgroundService.CoreService.DeviceManagement;

/// <summary>コンソールの一回限りの補助プロセスと確認済みpipeで連絡します。</summary>
[SupportedOSPlatform("windows")]
internal static class ConsoleSettingsAccess
{
    /// <summary>pipeへ接続した実PIDを検証し、子プロセスを必ず回収します。</summary>
    /// <returns>固定した補助プロセスだけが返す応答です。</returns>
    internal static async Task<ConsoleSettingsWorker.Reply> ExchangeAsync(ConsoleSettingsWorker.Request request, CancellationToken cancellationToken = default)
    {
        string name = "contents-device-" + Guid.NewGuid().ToString("N");
        using NamedPipeServerStream pipe = DesktopWorkerProcess.CreatePipe(name);
        using Process process = DesktopWorkerProcess.StartDisplay(name);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await pipe.WaitForConnectionAsync(deadline.Token).ConfigureAwait(false);
            DesktopWorkerProcess.Verify(pipe, process);
            await ConsoleSettingsWorker.WriteAsync(pipe, request, deadline.Token).ConfigureAwait(false);
            ConsoleSettingsWorker.Reply reply = await ConsoleSettingsWorker.ReadAsync<ConsoleSettingsWorker.Reply>(pipe, deadline.Token).ConfigureAwait(false);
            if (reply.Problem is not null)
            {
                throw new InvalidOperationException(reply.Problem);
            }
            return reply;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
            await ProcessCompletion.WaitAsync(process, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
