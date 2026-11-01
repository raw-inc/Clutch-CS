using ReArtWord.Contents.BackgroundService.Runtime.Processes;
using System.Diagnostics;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests;

/// <summary>試験が起動したプロセスを、他の回収処理の失敗にかかわらず終了まで待ちます。</summary>
internal static class TestProcessCleanup
{
    /// <summary>専用配置のプロセスを再走査し、更新で入れ替わった子も終了まで回収します。</summary>
    /// <returns>配置内のプロセスがなくなるまでの回収です。</returns>
    internal static async Task StopDirectoryAsync(string directory)
    {
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase
                                                                  : StringComparison.Ordinal;
        int remaining;
        do
        {
            List<Process> owned = [];
            foreach (Process process in Process.GetProcesses())
            {
                bool matched = false;
                try
                {
                    if (process.MainModule?.FileName is string executable && Path.GetFullPath(executable).StartsWith(prefix, comparison))
                    {
                        _ = process.SafeHandle;
                        owned.Add(process);
                        matched = true;
                    }
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // 列挙中に終了した実体や、照会できない別ユーザーの実体には触れません。
                }
                finally
                {
                    if (!matched)
                    {
                        process.Dispose();
                    }
                }
            }
            remaining = owned.Count;
            await StopAsync(owned.ToArray()).ConfigureAwait(false);
        }
        while (remaining != 0);
    }

    /// <summary>各プロセスの停止を開始し、すべての回収が終わってから失敗を伝えます。</summary>
    /// <returns>プロセスと所有ハンドルの回収です。</returns>
    internal static Task StopAsync(params Process?[] processes) => Task.WhenAll(processes.Select(StopOneAsync));

    /// <summary>所有する実体の終了を確認してから、ハンドルを解放します。</summary>
    /// <returns>一つのプロセスの回収です。</returns>
    private static async Task StopOneAsync(Process? process)
    {
        if (process is null)
        {
            return;
        }
        try
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(true);
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception && process.HasExited)
                {
                    // 停止要求と自然終了が重なった場合も、終了確認まで進めます。
                }
            }
            await ProcessCompletion.WaitAsync(process, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            process.Dispose();
        }
    }
}
