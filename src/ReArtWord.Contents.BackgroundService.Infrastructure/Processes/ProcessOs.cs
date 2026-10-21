using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Runtime.Processes;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Processes;

/// <summary>明示したPIDと起動時刻を照合してOS プロセスを操作します。</summary>
internal static partial class ProcessOs
{
    /// <summary>現在の実プロセスをPIDと起動時刻で識別します。</summary>
    /// <returns>操作履歴書込元の実体です。</returns>
    internal static ProcessIdentity CurrentIdentity()
    {
        using Process process = Process.GetCurrentProcess();
        return Identity(process);
    }

    /// <summary>登録済みの引数をshellを通さず起動します。</summary>
    /// <returns>実際に起動したプロセスの識別子です。</returns>
    internal static ProcessIdentity Start(ProcessDefinition definition)
    {
        using Process process = StartTracked(definition);
        return Identity(process);
    }

    /// <summary>終了コードを継続観測する所有元へ起動ハンドルを渡します。</summary>
    /// <returns>呼出元が所有して解放する実プロセスのハンドルです。</returns>
    // 抑制理由: 起動失敗時だけハンドルを回収し、元の例外と副作用の未確認状態を維持します。
    [SuppressMessage("SourceQuality", "RAW330", Justification = "起動失敗時の資源回収後も、元の起動エラーを保存境界へ伝えます。")]
    internal static Process StartTracked(ProcessDefinition definition)
    {
        if (!Path.IsPathFullyQualified(definition.ExecutablePath) || !File.Exists(definition.ExecutablePath) || !Path.IsPathFullyQualified(definition.WorkingDirectory) || !Directory.Exists(definition.WorkingDirectory))
        {
            throw new FileNotFoundException("登録済み実行物または作業ディレクトリがありません。");
        }
        ProcessStartInfo start = new(definition.ExecutablePath)
        {
            UseShellExecute = false,

            // LEGACY-FORMAT: application-launch-unspecified — 未指定の旧登録は従来のコンソール非表示を維持します。
            CreateNoWindow = definition.Launch?.Window != EProcessWindowMode.Default,
            WindowStyle = definition.Launch?.Window switch { EProcessWindowMode.NoWindow => ProcessWindowStyle.Hidden, _ => ProcessWindowStyle.Normal },
            WorkingDirectory = definition.WorkingDirectory,
        };
        foreach (string argument in definition.Arguments)
        {
            start.ArgumentList.Add(argument);
        }
        Process process = new() { StartInfo = start };
        try
        {
            if (!process.Start())
            {
                throw new IOException("登録processを起動できませんでした。");
            }
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    /// <summary>PID再利用を区別して実生存を確認します。</summary>
    /// <returns>生存、終了、または取得不能のnullです。</returns>
    internal static bool? IsRunning(ProcessIdentity? identity)
    {
        if (identity is null)
        {
            return false;
        }
        try
        {
            using Process process = Process.GetProcessById(identity.ProcessId);
            return !process.HasExited && Identity(process) == identity;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>通常終了後の明示方針を適用し対象プロセス自身の終了を確認します。</summary>
    /// <returns>終了、手動対応待ち、または停止結果不明です。</returns>
    internal static Task<EOutcome> StopAsync(ProcessIdentity identity, EProcessStopPolicy policy, int timeoutMilliseconds, CancellationToken cancellationToken = default)
        => StopAsync(identity, policy, timeoutMilliseconds, new DefaultProcessPlatform(), cancellationToken);

    /// <summary>アプリケーションのOS境界で通常終了を要求し、その後の実終了を確認します。</summary>
    /// <returns>指定した停止方針での確認結果です。</returns>
    internal static async Task<EOutcome> StopAsync(ProcessIdentity identity, EProcessStopPolicy policy, int timeoutMilliseconds, IProcessPlatform platform, CancellationToken cancellationToken = default)
    {
        bool? running = IsRunning(identity);
        if (running == false)
        {
            return EOutcome.Succeeded;
        }
        if (running is null)
        {
            return EOutcome.Unknown;
        }
        try
        {
            using Process process = Process.GetProcessById(identity.ProcessId);
            if (Identity(process) != identity)
            {
                return EOutcome.Unknown;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (OperatingSystem.IsWindows())
            {
                _ = await platform.CloseWindowAsync(identity, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
                {
                    return EOutcome.NotExecuted;
                }
                if (SendSignal(identity.ProcessId, 15) != 0)
                {
                    if (IsRunning(identity) == false)
                    {
                        return EOutcome.Succeeded;
                    }
                    return EOutcome.Unknown;
                }
            }
            using CancellationTokenSource grace = new(TimeSpan.FromMilliseconds(timeoutMilliseconds));
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(grace.Token, cancellationToken);
            bool timedOut = false;
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                timedOut = !cancellationToken.IsCancellationRequested;
            }
            if (process.HasExited)
            {
                return EOutcome.Succeeded;
            }
            if (!timedOut)
            {
                return EOutcome.Unknown;
            }
            if (policy == EProcessStopPolicy.WaitForManual)
            {
                return EOutcome.Failed;
            }
            if (Identity(process) != identity)
            {
                return EOutcome.Unknown;
            }
            process.Kill(false);
            using CancellationTokenSource forced = new(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(forced.Token).ConfigureAwait(false);
            return EOutcome.Succeeded;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or OperationCanceledException or NotSupportedException)
        {
            bool? stopped = IsRunning(identity);
            if (stopped == false)
            {
                return EOutcome.Succeeded;
            }
            return EOutcome.Unknown;
        }
    }

    /// <summary>OSが公開する起動時刻とPIDを一つの識別子にします。</summary>
    /// <returns>別のプロセスに再利用されるPIDだけでは一致しない識別子です。</returns>
    // 抑制理由: BCLのProcess.StartTimeだけがOS起動時刻を公開するため、取得直後にUTCのDateTimeOffsetへ変換します。
    [SuppressMessage("SourceQuality", "RAW171", Justification = "PID再利用防止に必要なBCL起動時刻を直ちにDateTimeOffsetへ変換します。")]
    internal static ProcessIdentity Identity(Process process)
    {
        return new ProcessIdentity(process.Id, new DateTimeOffset(process.StartTime.ToUniversalTime()));
    }

    /// <summary>照合したUnix プロセスへ通常終了のSIGTERMを通知します。</summary>
    /// <returns>成功は0、権限不足や消失は非0です。</returns>
    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int SendSignal(int processId, int signal);
}
