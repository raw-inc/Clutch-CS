using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using Porta.Pty;
using ReArtWord.Contents.BackgroundService.CoreService.Windows;

namespace ReArtWord.Contents.BackgroundService.CoreService.Terminal;

/// <summary>PTY補助プロセスの起動と、ユーザーシェルの実際の作成成功を区別します。</summary>
internal static class TerminalStartup
{
    /// <summary>権限切替が必要なWindowsでは認証した補助プロセスの作成結果を待ちます。</summary>
    /// <returns>起動確認済みのPTYです。失敗した一回は回収します。</returns>
    // 抑制理由: 成功時だけPTYの所有権を呼出元へ移し、失敗時はfinallyで回収します。
    [SuppressMessage("SourceQuality", "RAW141", Justification = "成功したPTYだけを呼出元へ移し、失敗時はfinallyで破棄します。")]
    internal static async Task<(IPtyConnection Process, bool System)> SpawnAsync(PtyOptions options, bool elevated, bool strictUserToken, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() || !WindowsInteractiveIdentity.IsAdministrator())
        {
            TerminalIdentity.Configure(options, elevated);
            return (await PtyProvider.SpawnAsync(options, cancellationToken).ConfigureAwait(false), false);
        }
        string name = "contents-terminal-start-" + Guid.NewGuid().ToString("N");
        using NamedPipeServerStream pipe = new(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
        bool system = TerminalIdentity.Configure(options, elevated, name, strictUserToken);
        IPtyConnection? process = await PtyProvider.SpawnAsync(options, cancellationToken).ConfigureAwait(false);
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            if (!WindowsProcessNative.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) || pid != process.Pid)
            {
                throw new IOException("The terminal startup pipe belongs to another process.");
            }
            byte[] response = new byte[sizeof(int)];
            await pipe.ReadExactlyAsync(response, timeout.Token).ConfigureAwait(false);
            int result = BinaryPrimitives.ReadInt32LittleEndian(response);
            if (result < 0)
            {
                throw new Win32Exception(-result);
            }
            if (result == 0)
            {
                throw new InvalidOperationException("The selected user or terminal privilege became unavailable before the shell started.");
            }
            IPtyConnection started = process;
            process = null;
            return (started, system);
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    process.Kill();
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
    }
}
