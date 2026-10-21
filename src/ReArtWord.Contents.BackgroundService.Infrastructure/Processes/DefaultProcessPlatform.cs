using System.Diagnostics;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Runtime.Processes;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Processes;

/// <summary>Core自身のOS実行環境で起動し、同じセッション内のウィンドウへ終了要求を渡します。</summary>
public sealed class DefaultProcessPlatform : IProcessPlatform
{
    /// <summary>別ユーザー起動が必要なWindows設定は、対応するOS境界がなければ拒否します。</summary>
    /// <returns>監視側が破棄する実プロセスです。</returns>
    public Process Start(ProcessDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (OperatingSystem.IsWindows() && definition.Launch?.Session == EProcessLaunchSession.Automatic)
        {
            throw new ProcessLaunchRejectedException("process.interactiveLaunchUnavailable");
        }
        return ProcessOs.StartTracked(definition);
    }

    /// <summary>PID再利用を区別して、通常のウィンドウ終了要求を送ります。</summary>
    /// <returns>要求の送信結果です。</returns>
    public ValueTask<bool> CloseWindowAsync(ProcessIdentity identity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        cancellationToken.ThrowIfCancellationRequested();
        using Process process = Process.GetProcessById(identity.ProcessId);
        return ValueTask.FromResult(ProcessOs.Identity(process) == identity && process.CloseMainWindow());
    }
}
