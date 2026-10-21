using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Security.Cryptography;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;

namespace ReArtWord.Contents.BackgroundService.CoreService.Hosting;

/// <summary>個別セッションの障害を記録し、他のセッションやCoreの寿命から隔離します。</summary>
internal sealed class SessionFailureBoundary(IWorkflowLog log, string peerId)
{
    /// <summary>通常取消を区別し、非致命的な失敗だけをセッションの終了として扱います。</summary>
    /// <returns>処理または失敗の記録が完了したことです。</returns>
    // 抑制理由: セッション所有境界では例外の列挙漏れをCore停止へ波及させません。致命的例外は除外します。
    [SuppressMessage("Design", "CA1031", Justification = "個別セッションの非致命的な失敗だけを記録し、所有者が後始末を続けます。"),
     SuppressMessage("SourceQuality", "RAW511", Justification = "代入直後に必ずawaitし、集約例外に致命的障害がないか確認するため同じTaskを保持します。")]
    internal async Task RunAsync(Func<Task> action, string component, Guid id, string phase, CancellationToken cancellationToken = default)
    {
        Task? operation = null;
        try
        {
            operation = action();
            await operation.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && operation?.IsFaulted != true)
        {
            // この所有者が要求した通常停止です。
        }
        catch (Exception error) when (IsRecoverable(error) && (operation?.Exception is null || IsRecoverable(operation.Exception)))
        {
            await RecordAsync(error, component, id, phase, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>通信を停止して完了を待ち、ある資源の解放が失敗しても残りを回収します。</summary>
    /// <returns>一接続の通信、同期資源、許可値の回収です。</returns>
    // 抑制理由: 所有資源の回収は呼出元の取消で中断せず、内部の寿命取消と完了確認を最後まで行います。
    [SuppressMessage("SourceQuality", "RAW510", Justification = "後始末の途中取消を許さず、所有する処理の停止確認と全資源の回収を完了します。")]
    internal async Task CloseAsync(string component, Guid id, TcpListener listener, CancellationTokenSource lifetime, Task completion, byte[] secret, params IDisposable[] resources)
    {
        try
        {
            await RunAsync(lifetime.CancelAsync, component, id, "cancel").ConfigureAwait(false);
            await RunAsync(() =>
            {
                listener.Stop();
                return Task.CompletedTask;
            }, component, id, "listener").ConfigureAwait(false);
            await RunAsync(() => completion, component, id, "completion", lifetime.Token).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                foreach (IDisposable resource in resources.Prepend(listener))
                {
                    await RunAsync(() =>
                    {
                        resource.Dispose();
                        return Task.CompletedTask;
                    }, component, id, "release").ConfigureAwait(false);
                }
            }
            finally
            {
                lifetime.Dispose();
                CryptographicOperations.ZeroMemory(secret);
            }
        }
    }

    /// <summary>集約例外や内部例外に含まれるメモリ枯渇・実行基盤の破損も回復扱いにしません。</summary>
    /// <returns>個別処理の失敗として終了できるかです。</returns>
    internal static bool IsRecoverable(Exception error)
    {
        if (error is OutOfMemoryException or StackOverflowException or AccessViolationException)
        {
            return false;
        }
        if (error is AggregateException aggregate)
        {
            return aggregate.InnerExceptions.All(IsRecoverable);
        }
        return error.InnerException is null || IsRecoverable(error.InnerException);
    }

    /// <summary>例外本文・トークン・コマンド・キャプチャ内容を含めず、種類と発生段階だけを残します。</summary>
    /// <returns>通常ログ、またはログ経路故障時の標準エラーへの記録です。</returns>
    // 抑制理由: 記録経路の非致命的な障害でも、セッションの後始末を継続します。
    [SuppressMessage("Design", "CA1031", Justification = "ログ故障を再帰記録せず、秘密を含まない診断へ一度だけフォールバックします。")]
    private async Task RecordAsync(Exception error, string component, Guid id, string phase, CancellationToken cancellationToken = default)
    {
        string exceptionType = error.GetType().Name;
        try
        {
            await log.WriteAsync(new WorkflowLogEntry(Guid.NewGuid(), DateTimeOffset.UtcNow, "WARN", peerId, "Session.Failed", CausationId: id,
                Data: NodeValue.FromObject(new { component, phase, exceptionType })), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception loggingError) when (IsRecoverable(loggingError))
        {
            try
            {
                await Console.Error.WriteLineAsync($"Session.Failed {component} {id:D} {phase} {exceptionType}").ConfigureAwait(false);
            }
            catch (Exception fallbackError) when (IsRecoverable(fallbackError))
            {
                // 記録不能を再帰的に記録しません。
            }
        }
    }
}
